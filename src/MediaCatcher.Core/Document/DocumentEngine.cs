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

    public DocumentEngine(
        HttpClient? client = null,
        IAppLogger? logger = null,
        IDocumentResolver? resolver = null)
    {
        _logger = logger ?? NullAppLogger.Instance;
        _client = client ?? CreateDefaultClient();
        _resolver = resolver ?? new FlipbookPageResolver(_client);
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
            var resolution = await _resolver.ResolveAsync(request.Url, cancellationToken).ConfigureAwait(false);
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
            results[index] = ToPdfImage(bytes);
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

    private static PdfImageInput ToPdfImage(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            var (width, height) = PdfAssembler.ReadJpegDimensions(bytes);
            return new PdfImageInput(width, height, bytes, PdfImageKind.Jpeg);
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
                "A page image is in an unsupported format (expected JPEG or PNG).",
                exception);
        }
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
