using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using MediaCatcher.Contracts;
using MediaCatcher.Core.Logging;
using MediaCatcher.Core.Models;
using MediaCatcher.Core.Utils;

namespace MediaCatcher.Core.YtDlp;

public sealed record YtDlpOutcome
{
    public bool Success { get; init; }

    public string? FilePath { get; init; }

    public string? Title { get; init; }

    public long TotalBytes { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }
}

public sealed record YtDlpUpdateResult(bool Success, string Message);

public sealed class YtDlpEngine
{
    private const int MaxPlaylistItems = 500;
    private const int MaxFormats = 120;
    private static readonly TimeSpan AnalyzeTimeout = TimeSpan.FromSeconds(110);

    private readonly ToolLocator _locator;
    private readonly IAppLogger _logger;

    public YtDlpEngine(ToolLocator? locator = null, IAppLogger? logger = null)
    {
        _locator = locator ?? new ToolLocator();
        _logger = logger ?? NullAppLogger.Instance;
    }

    public Task<ToolStatus> GetToolStatusAsync(CancellationToken cancellationToken = default) =>
        _locator.GetStatusAsync(cancellationToken);

    public async Task<AnalyzeResult> AnalyzeAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!UrlPolicy.IsSupported(url))
        {
            throw new DownloadException(ErrorCodes.UnsupportedUrl, "Only http and https URLs can be analyzed.");
        }

        var ytDlp = ResolveYtDlpOrThrow();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AnalyzeTimeout);

        try
        {
            var flatResult = await RunCaptureAsync(
                ytDlp,
                YtDlpArgumentBuilder.BuildAnalyzeArguments(url, flatPlaylist: true),
                timeout.Token).ConfigureAwait(false);

            if (flatResult.ExitCode != 0)
            {
                throw MapYtDlpError(flatResult.StandardError, flatResult.ExitCode);
            }

            using var flatDocument = ParseJson(flatResult.StandardOutput);
            var root = flatDocument.RootElement;

            if (IsPlaylist(root))
            {
                return MapPlaylist(root);
            }

            var detailResult = flatResult;
            if (!HasFormats(root))
            {
                detailResult = await RunCaptureAsync(
                    ytDlp,
                    YtDlpArgumentBuilder.BuildAnalyzeArguments(url, flatPlaylist: false),
                    timeout.Token).ConfigureAwait(false);

                if (detailResult.ExitCode != 0)
                {
                    throw MapYtDlpError(detailResult.StandardError, detailResult.ExitCode);
                }
            }

            using var detailDocument = ParseJson(detailResult.StandardOutput);
            return MapVideo(detailDocument.RootElement);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DownloadException(ErrorCodes.ExtractionFailed, "Analysis timed out.");
        }
    }

    public async Task<YtDlpOutcome> DownloadAsync(
        YtDlpDownloadRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!UrlPolicy.IsSupported(request.Url))
        {
            return Failure(ErrorCodes.UnsupportedUrl, "Only http and https URLs can be downloaded.");
        }

        var paths = _locator.Resolve();
        if (paths.YtDlpPath is null)
        {
            return Failure(ErrorCodes.ToolMissing, "yt-dlp was not found. Run scripts/fetch-tools.ps1 or set the tool path in Settings.");
        }

        Directory.CreateDirectory(request.OutputDirectory);

        string? temporaryCookieFile = null;
        try
        {
            var effectiveRequest = request;
            if (!string.IsNullOrWhiteSpace(request.Cookies))
            {
                temporaryCookieFile = WriteTemporaryCookieFile(request.Cookies);
                effectiveRequest = request with { CookiesFile = temporaryCookieFile };
            }

            var arguments = YtDlpArgumentBuilder.BuildDownloadArguments(
                effectiveRequest with
                {
                    FfmpegDirectory = effectiveRequest.FfmpegDirectory
                        ?? (paths.FfmpegPath is null ? null : Path.GetDirectoryName(paths.FfmpegPath))
                });

            _logger.Info("yt-dlp " + FileAppLogger.RedactSecrets(string.Join(' ', arguments)));

            string? destination = null;
            string? stage = null;
            var errors = new List<string>();
            var createdAt = DateTime.UtcNow.AddSeconds(-5);

            var exitCode = await RunStreamingAsync(
                paths.YtDlpPath,
                arguments,
                line =>
                {
                    var parsed = YtDlpProgressParser.ParseLine(line);
                    switch (parsed.Kind)
                    {
                        case YtDlpLineKind.Progress when parsed.Progress is not null:
                            progress?.Report(parsed.Progress with { Stage = stage ?? "Downloading" });
                            break;
                        case YtDlpLineKind.Destination when parsed.Destination is not null:
                            destination = ResolveDestination(parsed.Destination, request.OutputDirectory);
                            if (parsed.Stage is not null)
                            {
                                stage = parsed.Stage;
                            }

                            break;
                        case YtDlpLineKind.Stage when parsed.Stage is not null:
                            stage = parsed.Stage;
                            break;
                        case YtDlpLineKind.Error when parsed.Error is not null:
                            errors.Add(parsed.Error);
                            _logger.Warn(FileAppLogger.RedactSecrets(parsed.Error));
                            break;
                        case YtDlpLineKind.Other when YtDlpProgressParser.LooksLikeDestination(line):
                            var printedPath = ResolveDestination(line.Trim(), request.OutputDirectory);
                            if (printedPath is not null && File.Exists(printedPath))
                            {
                                destination = printedPath;
                            }

                            break;
                    }
                },
                errorLine =>
                {
                    if (!string.IsNullOrWhiteSpace(errorLine))
                    {
                        errors.Add(errorLine);
                        _logger.Warn(FileAppLogger.RedactSecrets(errorLine));
                    }
                },
                cancellationToken).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (exitCode != 0)
            {
                var mapped = MapYtDlpError(string.Join('\n', errors), exitCode);
                return Failure(mapped.Code, mapped.Message);
            }

            var finalPath = destination;
            if (finalPath is null || !File.Exists(finalPath))
            {
                finalPath = FindNewestOutputFile(request.OutputDirectory, createdAt);
            }

            if (finalPath is null || !File.Exists(finalPath))
            {
                return Failure(
                    ErrorCodes.ExtractionFailed,
                    "yt-dlp finished but no output file was found.");
            }

            var fileSize = new FileInfo(finalPath).Length;
            progress?.Report(new DownloadProgress
            {
                DownloadedBytes = fileSize,
                TotalBytes = fileSize,
                SpeedBps = 0,
                Stage = "Completed",
                ActiveSegments = 0
            });

            _logger.Info($"yt-dlp completed: {finalPath}");
            return new YtDlpOutcome
            {
                Success = true,
                FilePath = finalPath,
                TotalBytes = fileSize
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DownloadException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (Exception exception)
        {
            _logger.Error("yt-dlp download failed.", exception);
            return Failure(ErrorCodes.ExtractionFailed, exception.Message);
        }
        finally
        {
            if (temporaryCookieFile is not null)
            {
                try
                {
                    File.Delete(temporaryCookieFile);
                }
                catch (IOException)
                {
                    // Best-effort cleanup.
                }
            }
        }
    }

    public async Task<YtDlpUpdateResult> UpdateAsync(CancellationToken cancellationToken = default)
    {
        var paths = _locator.Resolve();
        if (paths.YtDlpPath is null)
        {
            return new YtDlpUpdateResult(false, "yt-dlp was not found.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));

        try
        {
            var result = await RunCaptureAsync(paths.YtDlpPath, ["-U"], timeout.Token).ConfigureAwait(false);
            var message = (result.StandardOutput + "\n" + result.StandardError).Trim();
            return new YtDlpUpdateResult(result.ExitCode == 0, message);
        }
        catch (OperationCanceledException)
        {
            return new YtDlpUpdateResult(false, "Update timed out.");
        }
    }

    private string ResolveYtDlpOrThrow()
    {
        var paths = _locator.Resolve();
        return paths.YtDlpPath
            ?? throw new DownloadException(
                ErrorCodes.ToolMissing,
                "yt-dlp was not found. Run scripts/fetch-tools.ps1 or set the tool path in Settings.");
    }

    private static bool IsPlaylist(JsonElement root)
    {
        var type = GetString(root, "_type");
        if (string.Equals(type, "playlist", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (root.TryGetProperty("entries", out var entries) &&
            entries.ValueKind == JsonValueKind.Array &&
            entries.GetArrayLength() > 0)
        {
            return true;
        }

        var count = GetLong(root, "playlist_count");
        return count > 1;
    }

    private static bool HasFormats(JsonElement root)
    {
        return root.TryGetProperty("formats", out var formats) &&
               formats.ValueKind == JsonValueKind.Array &&
               formats.GetArrayLength() > 0;
    }

    private static AnalyzeResult MapPlaylist(JsonElement root)
    {
        var entries = new List<AnalyzeEntry>();
        var actualCount = (int)(GetLong(root, "playlist_count") ?? 0);

        if (root.TryGetProperty("entries", out var entriesElement) &&
            entriesElement.ValueKind == JsonValueKind.Array)
        {
            var entryCount = 0;
            foreach (var entry in entriesElement.EnumerateArray())
            {
                if (entryCount >= MaxPlaylistItems)
                {
                    break;
                }

                entryCount++;
                var url = GetString(entry, "webpage_url") ?? GetString(entry, "url");
                if (string.IsNullOrWhiteSpace(url))
                {
                    var id = GetString(entry, "id");
                    var extractor = GetString(entry, "ie_key") ?? GetString(entry, "extractor_key") ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(id) &&
                        extractor.Contains("youtube", StringComparison.OrdinalIgnoreCase))
                    {
                        url = "https://www.youtube.com/watch?v=" + id;
                    }
                }

                if (string.IsNullOrWhiteSpace(url) ||
                    !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                entries.Add(new AnalyzeEntry
                {
                    Title = GetString(entry, "title"),
                    Url = url,
                    DurationSeconds = GetDouble(entry, "duration"),
                    ThumbnailUrl = GetThumbnail(entry)
                });
            }
        }

        if (actualCount <= 0)
        {
            actualCount = entries.Count;
        }

        return new AnalyzeResult
        {
            Title = GetString(root, "title"),
            ThumbnailUrl = GetThumbnail(root),
            Uploader = GetString(root, "uploader") ?? GetString(root, "channel"),
            IsPlaylist = true,
            ItemCount = actualCount,
            Entries = entries
        };
    }

    private static AnalyzeResult MapVideo(JsonElement root)
    {
        var formats = new List<MediaFormat>();

        if (root.TryGetProperty("formats", out var formatsElement) &&
            formatsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var format in formatsElement.EnumerateArray())
            {
                if (formats.Count >= MaxFormats)
                {
                    break;
                }

                var formatId = GetString(format, "format_id") ?? string.Empty;
                var extension = GetString(format, "ext");
                var protocol = GetString(format, "protocol");

                if (formatId.StartsWith("sb", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, "mhtml", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(protocol, "mhtml", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var height = (int?)GetLong(format, "height");
                var width = (int?)GetLong(format, "width");
                var fps = GetDouble(format, "fps");
                var videoCodec = GetString(format, "vcodec");
                var audioCodec = GetString(format, "acodec");
                var fileSize = GetLong(format, "filesize") ?? GetLong(format, "filesize_approx");
                var totalBitrate = GetDouble(format, "tbr");
                var audioBitrate = GetDouble(format, "abr");
                var audioOnly = IsNone(videoCodec) && !IsNone(audioCodec);

                formats.Add(new MediaFormat
                {
                    FormatId = formatId,
                    Label = BuildFormatLabel(height, width, fps, extension, fileSize, audioBitrate, audioOnly),
                    Extension = extension,
                    Width = width,
                    Height = height,
                    Fps = fps,
                    VideoCodec = videoCodec,
                    AudioCodec = audioCodec,
                    FileSizeBytes = fileSize,
                    TotalBitrate = totalBitrate,
                    AudioOnly = audioOnly
                });
            }
        }

        var ordered = formats
            .OrderByDescending(format => format.AudioOnly ? 0 : 1)
            .ThenByDescending(format => format.Height ?? 0)
            .ThenByDescending(format => format.TotalBitrate ?? 0)
            .ToList();

        return new AnalyzeResult
        {
            Title = GetString(root, "title"),
            DurationSeconds = GetDouble(root, "duration"),
            ThumbnailUrl = GetThumbnail(root),
            Uploader = GetString(root, "uploader") ?? GetString(root, "channel"),
            IsPlaylist = false,
            ItemCount = 1,
            Formats = ordered
        };
    }

    private static string BuildFormatLabel(
        int? height,
        int? width,
        double? fps,
        string? extension,
        long? fileSize,
        double? audioBitrate,
        bool audioOnly)
    {
        var parts = new List<string>();
        if (height is > 0)
        {
            parts.Add($"{height}p");
        }
        else if (width is > 0)
        {
            parts.Add($"{width}x{height}");
        }
        else if (audioOnly)
        {
            parts.Add(audioBitrate is > 0 ? $"Audio {audioBitrate:0}kbps" : "Audio");
        }

        if (fps is > 30)
        {
            parts.Add($"{fps:0}fps");
        }

        if (!string.IsNullOrWhiteSpace(extension))
        {
            parts.Add(extension.ToUpperInvariant());
        }

        if (fileSize is > 0)
        {
            parts.Add(FilenamePolicy.ToDisplaySize(fileSize.Value));
        }

        return parts.Count == 0 ? "Format" : string.Join(" · ", parts);
    }

    private static bool IsNone(string? codec) =>
        string.IsNullOrWhiteSpace(codec) || codec.Equals("none", StringComparison.OrdinalIgnoreCase);

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static double? GetDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var value))
        {
            return value;
        }

        if (property.ValueKind == JsonValueKind.String &&
            double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return value;
        }

        return null;
    }

    private static long? GetLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var value))
        {
            return value;
        }

        if (property.ValueKind == JsonValueKind.String &&
            long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return value;
        }

        return null;
    }

    private static string? GetThumbnail(JsonElement element)
    {
        var direct = GetString(element, "thumbnail");
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        if (element.TryGetProperty("thumbnails", out var thumbnails) &&
            thumbnails.ValueKind == JsonValueKind.Array)
        {
            string? last = null;
            foreach (var thumbnail in thumbnails.EnumerateArray())
            {
                var url = GetString(thumbnail, "url");
                if (!string.IsNullOrWhiteSpace(url))
                {
                    last = url;
                }
            }

            return last;
        }

        return null;
    }

    private static JsonDocument ParseJson(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new DownloadException(
                ErrorCodes.ExtractionFailed,
                "yt-dlp returned invalid metadata JSON.",
                exception);
        }
    }

    private string? ResolveDestination(string? candidate, string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var trimmed = candidate.Trim().Trim('"');
        if (Path.IsPathRooted(trimmed))
        {
            return trimmed;
        }

        var combined = Path.Combine(outputDirectory, trimmed);
        return combined;
    }

    private static string? FindNewestOutputFile(string directory, DateTime createdAfterUtc)
    {
        try
        {
            return Directory
                .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(path =>
                    !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase) &&
                    !path.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase) &&
                    !path.EndsWith(".temp", StringComparison.OrdinalIgnoreCase))
                .Select(path => new FileInfo(path))
                .Where(info => info.LastWriteTimeUtc >= createdAfterUtc)
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .Select(info => info.FullName)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string WriteTemporaryCookieFile(string cookies)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MediaCatcher");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"cookies-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, cookies, new UTF8Encoding(false));
        return path;
    }

    private async Task<ProcessResult> RunCaptureAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = StartProcess(executable, arguments);
        using var registration = cancellationToken.Register(() => KillProcessTree(process));

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Process already gone.
            }

            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private async Task<int> RunStreamingAsync(
        string executable,
        IReadOnlyList<string> arguments,
        Action<string> onStdout,
        Action<string> onStderr,
        CancellationToken cancellationToken)
    {
        using var process = StartProcess(executable, arguments);
        using var registration = cancellationToken.Register(() => KillProcessTree(process));

        var stdoutTask = PumpAsync(process.StandardOutput, onStdout);
        var stderrTask = PumpAsync(process.StandardError, onStderr);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Process already gone.
            }

            throw;
        }

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        return process.ExitCode;
    }

    private static Process StartProcess(string executable, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                throw new DownloadException(ErrorCodes.ToolMissing, $"Unable to start {Path.GetFileName(executable)}.");
            }
        }
        catch (Win32Exception exception)
        {
            process.Dispose();
            throw new DownloadException(
                ErrorCodes.ToolMissing,
                $"Unable to start {Path.GetFileName(executable)}: {exception.Message}",
                exception);
        }

        return process;
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onLine)
    {
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (IOException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (line is null)
            {
                break;
            }

            onLine(line);
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (Win32Exception)
        {
            // Access denied while terminating; the caller still stops waiting.
        }
        catch (NotSupportedException)
        {
            // Platform lacks process-tree termination.
        }
    }

    private static DownloadException MapYtDlpError(string errorText, int exitCode)
    {
        var normalized = (errorText ?? string.Empty).ToLowerInvariant();
        if (normalized.Contains("drm", StringComparison.Ordinal) ||
            normalized.Contains("widevine", StringComparison.Ordinal))
        {
            return new DownloadException(
                ErrorCodes.DrmUnsupported,
                "This content is DRM-protected and is not supported.");
        }

        if (normalized.Contains("ffmpeg", StringComparison.Ordinal) &&
            (normalized.Contains("not found", StringComparison.Ordinal) ||
             normalized.Contains("not installed", StringComparison.Ordinal) ||
             normalized.Contains("could not find", StringComparison.Ordinal)))
        {
            return new DownloadException(
                ErrorCodes.ToolMissing,
                "ffmpeg is required for this operation. Run scripts/fetch-tools.ps1.");
        }

        if (normalized.Contains("sign in", StringComparison.Ordinal) ||
            normalized.Contains("login required", StringComparison.Ordinal) ||
            normalized.Contains("cookies", StringComparison.Ordinal))
        {
            return new DownloadException(
                ErrorCodes.ExtractionFailed,
                "This site requires sign-in. Enable cookies or sign in with your browser.");
        }

        var lastLine = (errorText ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();

        return new DownloadException(
            ErrorCodes.ExtractionFailed,
            lastLine ?? $"yt-dlp exited with code {exitCode}.");
    }

    private static YtDlpOutcome Failure(string code, string message) =>
        new()
        {
            Success = false,
            ErrorCode = code,
            ErrorMessage = message
        };

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
