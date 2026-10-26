using System.Diagnostics;
using MediaCatcher.Core.Logging;
using MediaCatcher.Core.Storage;

namespace MediaCatcher.Core.YtDlp;

public sealed record ToolPaths(string? YtDlpPath, string? FfmpegPath, string? FfprobePath)
{
    public bool HasYtDlp => !string.IsNullOrWhiteSpace(YtDlpPath);

    public bool HasFfmpeg => !string.IsNullOrWhiteSpace(FfmpegPath);
}

public sealed record ToolStatus(
    string? YtDlpPath,
    string? YtDlpVersion,
    string? FfmpegPath,
    string? FfmpegVersion)
{
    public bool YtDlpReady => !string.IsNullOrWhiteSpace(YtDlpPath);

    public bool FfmpegReady => !string.IsNullOrWhiteSpace(FfmpegPath);
}

public sealed class ToolLocator
{
    private readonly string? _ytDlpOverride;
    private readonly string? _ffmpegOverride;
    private readonly string _toolsDirectory;
    private readonly string? _pathEnvironment;
    private readonly IAppLogger _logger;

    public ToolLocator(
        string? ytDlpOverride = null,
        string? ffmpegOverride = null,
        string? toolsDirectory = null,
        string? pathEnvironment = null,
        IAppLogger? logger = null)
    {
        _ytDlpOverride = ytDlpOverride;
        _ffmpegOverride = ffmpegOverride;
        _toolsDirectory = toolsDirectory ?? Path.Combine(AppContext.BaseDirectory, "tools");
        _pathEnvironment = pathEnvironment ?? Environment.GetEnvironmentVariable("PATH");
        _logger = logger ?? NullAppLogger.Instance;
    }

    public static ToolLocator FromSettings(AppSettings settings, IAppLogger? logger = null) =>
        new(settings.YtDlpPath, settings.FfmpegPath, logger: logger);

    public ToolPaths Resolve()
    {
        var ytDlpPath = FindExecutable("yt-dlp.exe", _ytDlpOverride);
        var ffmpegPath = FindExecutable("ffmpeg.exe", _ffmpegOverride, ytDlpPath);
        var ffprobePath = FindExecutable("ffprobe.exe", null, ytDlpPath);
        return new ToolPaths(ytDlpPath, ffmpegPath, ffprobePath);
    }

    public async Task<ToolStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var paths = Resolve();
        string? ytDlpVersion = null;
        string? ffmpegVersion = null;

        if (paths.YtDlpPath is not null)
        {
            ytDlpVersion = await TryReadVersionAsync(
                paths.YtDlpPath,
                ["--version"],
                cancellationToken).ConfigureAwait(false);
        }

        if (paths.FfmpegPath is not null)
        {
            ffmpegVersion = await TryReadVersionAsync(
                paths.FfmpegPath,
                ["-version"],
                cancellationToken).ConfigureAwait(false);
            if (ffmpegVersion?.Contains("version", StringComparison.OrdinalIgnoreCase) == true)
            {
                var firstLine = ffmpegVersion.Split('\n')[0].Trim();
                ffmpegVersion = firstLine;
            }
        }

        return new ToolStatus(paths.YtDlpPath, ytDlpVersion, paths.FfmpegPath, ffmpegVersion);
    }

    private string? FindExecutable(string fileName, string? explicitPath, string? companionPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var candidate = ResolveCandidate(explicitPath, fileName);
            if (candidate is not null)
            {
                return candidate;
            }
        }

        if (companionPath is not null)
        {
            var companionDirectory = Path.GetDirectoryName(companionPath);
            if (companionDirectory is not null)
            {
                var candidate = Path.Combine(companionDirectory, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        var toolsCandidate = Path.Combine(_toolsDirectory, fileName);
        if (File.Exists(toolsCandidate))
        {
            return toolsCandidate;
        }

        var appCandidate = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(appCandidate))
        {
            return appCandidate;
        }

        if (!string.IsNullOrWhiteSpace(_pathEnvironment))
        {
            foreach (var directory in _pathEnvironment.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                try
                {
                    var candidate = Path.Combine(directory.Trim(), fileName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // Ignore malformed PATH entries.
                }
            }
        }

        return null;
    }

    private static string? ResolveCandidate(string explicitPath, string fileName)
    {
        try
        {
            if (Directory.Exists(explicitPath))
            {
                var combined = Path.Combine(explicitPath, fileName);
                return File.Exists(combined) ? Path.GetFullPath(combined) : null;
            }

            return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async Task<string?> TryReadVersionAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(output) ? error.Trim() : output.Trim();
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.Warn($"Unable to read version from {executable}: {exception.Message}");
            return null;
        }
    }
}
