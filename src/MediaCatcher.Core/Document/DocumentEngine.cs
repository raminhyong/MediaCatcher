using System.Diagnostics;
using System.Net;
using MediaCatcher.Contracts;
using MediaCatcher.Core.Logging;
using MediaCatcher.Core.Models;
using MediaCatcher.Core.Utils;

namespace MediaCatcher.Core.Document;

public sealed class DocumentEngine
{
    private const int MaxRetries = 3;

    private readonly HttpClient _client;
    private readonly IAppLogger _logger;
    private readonly IDocumentResolver _resolver;
    private readonly string? _ffmpegPath;

    public DocumentEngine(
        HttpClient? client = null,
        IAppLogger? logger = null,
        IDocumentResolver? resolver = null,
        string? ffmpegPath = null)
    {
        _logger = logger ?? NullAppLogger.Instance;
        _client = client ?? CreateDefaultClient();
        _resolver = resolver ?? new FlipbookPageResolver(_client);
        _ffmpegPath = ffmpegPath;
    }

    public async Task<DocumentOutcome> DownloadAsync(
        DocumentDownloadRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!UrlPolicy.IsSupported(request.Url))
        {
            return Failure(ErrorCodes.UnsupportedUrl, "Only http and https URLs can be downloaded.");
        }

        try
        {
            var resolution = await _resolver.ResolveAsync(request.Url, request.UserAgent, cancellationToken).ConfigureAwait(false);
            var pages = resolution.Pages
                .Take(Math.Clamp(request.MaxPages, 1, 2000))
                .ToList();

            if (pages.Count == 0)
            {
                return Failure(ErrorCodes.ExtractionFailed, "No pages were found for this document.");
            }

            _logger.Info($"Resolved {pages.Count} pages for {request.Url}");

            var images = await DownloadPagesAsync(request, pages, progress, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            var pdf = PdfAssembler.Build(images);
            var fileName = ResolveFileName(request.FileName, resolution.Title);
            Directory.CreateDirectory(request.OutputDirectory);
            var finalPath = FilenamePolicy.EnsureUniquePath(request.OutputDirectory, fileName);
            AtomicFile.WriteAllBytes(finalPath, pdf);

            progress?.Report(new DownloadProgress
            {
                DownloadedBytes = images.Count,
                TotalBytes = images.Count,
                SpeedBps = 0,
                Stage = "Completed",
                ActiveSegments = 0
            });

            _logger.Info($"Completed {finalPath} ({images.Count} pages)");

            return new DocumentOutcome
            {
                Success = true,
                FilePath = finalPath,
                Title = resolution.Title,
                PageCount = images.Count,
                TotalBytes = pdf.Length
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DownloadException exception)
        {
            _logger.Warn($"Document download failed ({exception.Code}): {exception.Message}");
            return Failure(exception.Code, exception.Message);
        }
        catch (Exception exception)
        {
            _logger.Error($"Document download failed: {request.Url}", exception);
            return Failure(ErrorCodes.ExtractionFailed, exception.Message);
        }
    }

    public async Task<DocumentOutcome> BuildPdfFromUrlsAsync(
        IReadOnlyList<string> urls,
        string outputDirectory,
        string? fileName = null,
        string? referer = null,
        string? userAgent = null,
        string? cookies = null,
        int connections = 4,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (urls is null || urls.Count == 0)
        {
            return Failure(ErrorCodes.InvalidRequest, "No image URLs were provided.");
        }

        try
        {
            var request = new DocumentDownloadRequest
            {
                Url = urls[0],
                OutputDirectory = outputDirectory,
                FileName = fileName,
                Referer = referer,
                UserAgent = userAgent,
                Cookies = cookies,
                Connections = Math.Clamp(connections, 1, 16)
            };

            var pages = urls
                .Where(UrlPolicy.IsSupported)
                .Select((url, index) => new DocumentPage
                {
                    Url = url,
                    Index = index,
                    Referer = referer
                })
                .ToList();

            if (pages.Count == 0)
            {
                return Failure(ErrorCodes.InvalidRequest, "No valid image URLs were provided.");
            }

            var images = await DownloadPagesAsync(request, pages, progress, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (images.Count == 0)
            {
                return Failure(ErrorCodes.ExtractionFailed, "No page images could be downloaded.");
            }

            var pdf = PdfAssembler.Build(images);
            var resolvedName = ResolveFileName(fileName, null);
            Directory.CreateDirectory(outputDirectory);
            var finalPath = FilenamePolicy.EnsureUniquePath(outputDirectory, resolvedName);
            AtomicFile.WriteAllBytes(finalPath, pdf);

            _logger.Info($"Completed {finalPath} ({images.Count} pages)");

            return new DocumentOutcome
            {
                Success = true,
                FilePath = finalPath,
                PageCount = images.Count,
                TotalBytes = pdf.Length
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DownloadException exception)
        {
            _logger.Warn($"PDF build failed ({exception.Code}): {exception.Message}");
            return Failure(exception.Code, exception.Message);
        }
        catch (Exception exception)
        {
            _logger.Error("PDF build failed.", exception);
            return Failure(ErrorCodes.ExtractionFailed, exception.Message);
        }
    }

    private async Task<IReadOnlyList<PdfImageInput>> DownloadPagesAsync(
        DocumentDownloadRequest request,
        IReadOnlyList<DocumentPage> pages,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var results = new PdfImageInput?[pages.Count];
        var completed = 0;

        using var gate = new SemaphoreSlim(Math.Clamp(request.Connections, 1, 16));
        var tasks = pages.Select((page, index) => DownloadPageAsync(
            request,
            page,
            index,
            results,
            () => Interlocked.Increment(ref completed),
            pages.Count,
            gate,
            progress,
            cancellationToken));

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var images = new List<PdfImageInput>(pages.Count);
        foreach (var image in results)
        {
            if (image is not null)
            {
                images.Add(image);
            }
        }

        return images;
    }

    private async Task DownloadPageAsync(
        DocumentDownloadRequest request,
        DocumentPage page,
        int index,
        PdfImageInput?[] results,
        Func<int> onCompleted,
        int total,
        SemaphoreSlim gate,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bytes = await DownloadBytesAsync(request, page, cancellationToken).ConfigureAwait(false);
            results[index] = await ToPdfImageAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        var done = onCompleted();
        progress?.Report(new DownloadProgress
        {
            DownloadedBytes = done,
            TotalBytes = total,
            SpeedBps = 0,
            Stage = "Downloading",
            ActiveSegments = total - done
        });
    }

    private async Task<byte[]> DownloadBytesAsync(
        DocumentDownloadRequest request,
        DocumentPage page,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var httpRequest = CreateRequest(page.Url, page.Referer ?? request.Referer, request);
                using var response = await _client
                    .SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false);

                var statusCode = (int)response.StatusCode;
                if (statusCode is 408 or 429 || statusCode >= 500)
                {
                    throw new HttpRequestException($"The server returned HTTP {statusCode}.");
                }

                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < MaxRetries)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
                attempt++;
            }
        }
    }

    private static HttpRequestMessage CreateRequest(
        string url,
        string? referer,
        DocumentDownloadRequest request)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
        httpRequest.Headers.TryAddWithoutValidation("Accept", "*/*");

        if (!string.IsNullOrWhiteSpace(request.UserAgent))
        {
            httpRequest.Headers.TryAddWithoutValidation("User-Agent", request.UserAgent);
        }

        if (!string.IsNullOrWhiteSpace(referer))
        {
            httpRequest.Headers.TryAddWithoutValidation("Referer", referer);
        }

        if (!string.IsNullOrWhiteSpace(request.Cookies))
        {
            httpRequest.Headers.TryAddWithoutValidation("Cookie", request.Cookies);
        }

        return httpRequest;
    }

    private static bool IsJpeg(byte[] bytes) =>
        bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xD8;

    private static bool IsWebp(byte[] bytes) =>
        bytes.Length >= 12 &&
        bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
        bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P';

    private async Task<PdfImageInput> ToPdfImageAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        if (IsJpeg(bytes))
        {
            var (width, height) = PdfAssembler.ReadJpegDimensions(bytes);
            return new PdfImageInput(width, height, bytes, PdfImageKind.Jpeg);
        }

        if (IsWebp(bytes))
        {
            var jpeg = await ConvertWebpToJpegAsync(bytes, cancellationToken).ConfigureAwait(false);
            var (width, height) = PdfAssembler.ReadJpegDimensions(jpeg);
            return new PdfImageInput(width, height, jpeg, PdfImageKind.Jpeg);
        }

        try
        {
            var png = PngDecoder.Decode(bytes);
            return new PdfImageInput(png.Width, png.Height, png.Rgb, PdfImageKind.Rgb);
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
        {
            throw new DownloadException(
                ErrorCodes.ExtractionFailed,
                "A page image is in an unsupported format (expected JPEG, PNG, or WebP).",
                exception);
        }
    }

    private async Task<byte[]> ConvertWebpToJpegAsync(byte[] webpBytes, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_ffmpegPath))
        {
            throw new DownloadException(
                ErrorCodes.ToolMissing,
                "ffmpeg is required to convert WebP pages. Run scripts/fetch-tools.ps1.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };

        var tempDir = Path.Combine(Path.GetTempPath(), "mediacatcher-webp");
        Directory.CreateDirectory(tempDir);
        var token = Guid.NewGuid().ToString("N");
        var inputPath = Path.Combine(tempDir, $"{token}.webp");
        var outputPath = Path.Combine(tempDir, $"{token}.jpg");

        try
        {
            await File.WriteAllBytesAsync(inputPath, webpBytes, cancellationToken).ConfigureAwait(false);

            startInfo.ArgumentList.Add("-y");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(inputPath);
            startInfo.ArgumentList.Add("-q:v");
            startInfo.ArgumentList.Add("2");
            startInfo.ArgumentList.Add(outputPath);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                throw new DownloadException(ErrorCodes.ToolMissing, "Unable to start ffmpeg.");
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                throw new DownloadException(ErrorCodes.ExtractionFailed, "ffmpeg failed to convert a WebP page.");
            }

            return await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFile(inputPath);
            TryDeleteFile(outputPath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore cleanup failures.
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static string ResolveFileName(string? requested, string? title)
    {
        var baseName = !string.IsNullOrWhiteSpace(requested)
            ? requested
            : (!string.IsNullOrWhiteSpace(title) ? title : "document");
        return FilenamePolicy.Sanitize(baseName, "pdf");
    }

    private static HttpClient CreateDefaultClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            MaxConnectionsPerServer = 64
        };

        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static DocumentOutcome Failure(string code, string message) =>
        new()
        {
            Success = false,
            ErrorCode = code,
            ErrorMessage = message
        };
}
