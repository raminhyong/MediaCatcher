using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using MediaCatcher.Contracts;
using MediaCatcher.Core.Logging;
using MediaCatcher.Core.Models;
using MediaCatcher.Core.Utils;
using Microsoft.Win32.SafeHandles;

namespace MediaCatcher.Core.Http;

public sealed class HttpDownloadEngine : IDisposable
{
    public const string PartSuffix = ".mediacatcher.part";
    public const string MetaSuffix = ".mediacatcher.meta";

    private const int BufferSize = 128 * 1024;
    private const int MaxRetries = 5;

    private readonly HttpClient _client;
    private readonly IAppLogger _logger;
    private readonly string? _defaultUserAgent;
    private readonly bool _ownsClient;

    public HttpDownloadEngine(
        string? proxyUrl = null,
        string? defaultUserAgent = null,
        IAppLogger? logger = null,
        HttpMessageHandler? handler = null)
    {
        _logger = logger ?? NullAppLogger.Instance;
        _defaultUserAgent = defaultUserAgent;

        if (handler is null)
        {
            var socketsHandler = new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.None,
                ConnectTimeout = TimeSpan.FromSeconds(20),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = 64
            };

            if (!string.IsNullOrWhiteSpace(proxyUrl) &&
                Uri.TryCreate(proxyUrl, UriKind.Absolute, out var proxyUri))
            {
                socketsHandler.Proxy = new WebProxy(proxyUri);
                socketsHandler.UseProxy = true;
            }

            _client = new HttpClient(socketsHandler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            _ownsClient = true;
        }
        else
        {
            _client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            _ownsClient = true;
        }
    }

    public Task<DownloadOutcome> DownloadAsync(
        DownloadJob job,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        DownloadAsync(job, progress, sharedBucket: null, cancellationToken);

    public async Task<DownloadOutcome> DownloadAsync(
        DownloadJob job,
        IProgress<DownloadProgress>? progress,
        TokenBucket? sharedBucket,
        CancellationToken cancellationToken = default)
    {
        if (!UrlPolicy.TryNormalize(job.Url, out var requestedUri, out var urlError))
        {
            return Failure(ErrorCodes.UnsupportedUrl, urlError ?? "Unsupported URL.");
        }

        string? finalPath = null;
        string? partPath = null;
        string? metaPath = null;
        ResumeMetadata? metadata = null;
        HttpProbeResult? probe = null;
        SegmentMetadata[]? segments = null;
        ProgressCounter? counter = null;
        SafeFileHandle? handle = null;
        Task? heartbeat = null;
        using var heartbeatCancellation = new CancellationTokenSource();

        try
        {
            probe = await ProbeAsync(job, cancellationToken).ConfigureAwait(false);
            _logger.Info(
                $"Probe {requestedUri.Host}: status={probe.StatusCode} bytes={probe.TotalBytes} " +
                $"ranges={probe.SupportsRanges} type={probe.ContentType}");

            var fileName = ResolveFileName(job, probe);
            var normalizedName = FilenamePolicy.Sanitize(
                fileName,
                CategoryResolver.DefaultExtensionFor(probe.ContentType ?? string.Empty));

            Directory.CreateDirectory(job.TargetDirectory);
            finalPath = FilenamePolicy.EnsureWithinRoot(
                job.TargetDirectory,
                Path.Combine(job.TargetDirectory, normalizedName));
            partPath = finalPath + PartSuffix;
            metaPath = finalPath + MetaSuffix;

            metadata = job.ForceRestart
                ? null
                : await TryLoadResumeMetadataAsync(metaPath, partPath, job, probe, cancellationToken)
                    .ConfigureAwait(false);

            if (metadata is null)
            {
                DeleteQuietly(partPath);
                DeleteQuietly(metaPath);
                finalPath = FilenamePolicy.EnsureUniquePath(job.TargetDirectory, normalizedName);
                partPath = finalPath + PartSuffix;
                metaPath = finalPath + MetaSuffix;
                metadata = new ResumeMetadata
                {
                    Url = job.Url,
                    FinalUrl = probe.FinalUrl,
                    TotalBytes = probe.TotalBytes,
                    ETag = probe.ETag,
                    LastModified = probe.LastModified,
                    Segments = []
                };
            }

            var canResume = metadata.DownloadedBytes > 0 && File.Exists(partPath);
            segments = canResume
                ? metadata.Segments.ToArray()
                : PlanSegments(probe, job.Connections);

            if (!canResume)
            {
                foreach (var segment in segments)
                {
                    segment.Downloaded = 0;
                }

                metadata.Segments = segments.ToList();
                metadata.TotalBytes = probe.TotalBytes;
                metadata.ETag = probe.ETag;
                metadata.LastModified = probe.LastModified;
                metadata.FinalUrl = probe.FinalUrl;
            }

            counter = new ProgressCounter(segments.Sum(segment => segment.Downloaded));
            var bucket = sharedBucket ?? (job.SpeedLimitBps > 0 ? new TokenBucket(job.SpeedLimitBps) : null);

            handle = File.OpenHandle(
                partPath,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.Read,
                FileOptions.Asynchronous | FileOptions.RandomAccess);

            if (probe.SupportsRanges && probe.TotalBytes > 0)
            {
                RandomAccess.SetLength(handle, probe.TotalBytes);
            }
            else if (!canResume)
            {
                RandomAccess.SetLength(handle, 0);
            }

            heartbeat = RunHeartbeatAsync(
                metadata,
                metaPath,
                counter,
                probe,
                segments,
                progress,
                heartbeatCancellation.Token);

            if (probe.SupportsRanges && probe.TotalBytes > 0)
            {
                var tasks = segments.Select(segment =>
                    DownloadSegmentAsync(
                        job,
                        segment,
                        segments.Length,
                        handle,
                        counter,
                        bucket,
                        probe.TotalBytes,
                        cancellationToken));
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            else
            {
                var segment = segments[0];
                await DownloadSingleStreamAsync(
                    job,
                    segment,
                    handle,
                    counter,
                    bucket,
                    cancellationToken).ConfigureAwait(false);
            }

            await heartbeatCancellation.CancelAsync().ConfigureAwait(false);
            await AwaitHeartbeatAsync(heartbeat).ConfigureAwait(false);
            heartbeat = null;

            await FlushMetadataAsync(metaPath, metadata, CancellationToken.None).ConfigureAwait(false);

            var downloaded = counter.Value;
            if (probe.TotalBytes > 0 && downloaded < probe.TotalBytes)
            {
                return Failure(
                    "INCOMPLETE",
                    $"Downloaded {downloaded} of {probe.TotalBytes} bytes.",
                    finalPath,
                    probe.FinalUrl,
                    probe.TotalBytes,
                    downloaded);
            }

            handle.Dispose();
            handle = null;

            if (probe.TotalBytes <= 0 && downloaded > 0)
            {
                using var finalHandle = File.OpenHandle(partPath, FileMode.Open, FileAccess.Write, FileShare.Read);
                RandomAccess.SetLength(finalHandle, downloaded);
            }

            if (File.Exists(finalPath))
            {
                File.Delete(finalPath);
            }

            File.Move(partPath, finalPath, overwrite: true);
            DeleteQuietly(metaPath);

            progress?.Report(new DownloadProgress
            {
                DownloadedBytes = downloaded,
                TotalBytes = probe.TotalBytes > 0 ? probe.TotalBytes : downloaded,
                SpeedBps = 0,
                Stage = "Completed",
                ActiveSegments = 0
            });

            _logger.Info($"Completed {finalPath} ({downloaded} bytes)");

            return new DownloadOutcome
            {
                Success = true,
                FilePath = finalPath,
                FinalUrl = probe.FinalUrl,
                TotalBytes = probe.TotalBytes > 0 ? probe.TotalBytes : downloaded,
                DownloadedBytes = downloaded,
                ETag = probe.ETag,
                LastModified = probe.LastModified,
                ContentType = probe.ContentType
            };
        }
        catch (OperationCanceledException)
        {
            if (metadata is not null && metaPath is not null)
            {
                await FlushMetadataAsync(metaPath, metadata, CancellationToken.None).ConfigureAwait(false);
            }

            _logger.Info($"Download canceled: {job.Url}");
            throw;
        }
        catch (DownloadException exception)
        {
            if (metadata is not null && metaPath is not null)
            {
                await FlushMetadataAsync(metaPath, metadata, CancellationToken.None).ConfigureAwait(false);
            }

            _logger.Warn($"Download failed ({exception.Code}): {exception.Message}");
            return Failure(
                exception.Code,
                exception.Message,
                finalPath,
                probe?.FinalUrl,
                probe?.TotalBytes ?? 0,
                counter?.Value ?? 0);
        }
        catch (Exception exception)
        {
            if (metadata is not null && metaPath is not null)
            {
                await FlushMetadataAsync(metaPath, metadata, CancellationToken.None).ConfigureAwait(false);
            }

            var mapped = MapException(exception);
            _logger.Error($"Download failed ({mapped.Code}): {job.Url}", exception);
            return Failure(
                mapped.Code,
                mapped.Message,
                finalPath,
                probe?.FinalUrl,
                probe?.TotalBytes ?? 0,
                counter?.Value ?? 0);
        }
        finally
        {
            await heartbeatCancellation.CancelAsync().ConfigureAwait(false);
            await AwaitHeartbeatAsync(heartbeat).ConfigureAwait(false);
            if (handle is not null)
            {
                handle.Dispose();
            }
        }
    }

    private async Task<HttpProbeResult> ProbeAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var ranged = await TryProbeAsync(job, sendRange: true, cancellationToken).ConfigureAwait(false);
                if (ranged is not null)
                {
                    return ranged;
                }

                var plain = await TryProbeAsync(job, sendRange: false, cancellationToken).ConfigureAwait(false);
                if (plain is not null)
                {
                    return plain;
                }

                throw new DownloadException(ErrorCodes.UnsupportedUrl, "The server did not return usable headers.");
            }
            catch (TransientProbeException exception) when (attempt < MaxRetries)
            {
                await DelayBeforeRetryAsync(attempt, exception.RetryAfter, cancellationToken).ConfigureAwait(false);
                attempt++;
            }
        }
    }

    private async Task<HttpProbeResult?> TryProbeAsync(
        DownloadJob job,
        bool sendRange,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(job, HttpMethod.Get, sendRange ? "bytes=0-0" : null);
        using var response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        var statusCode = (int)response.StatusCode;
        if (statusCode is 408 or 429 || statusCode >= 500)
        {
            throw new TransientProbeException(GetRetryAfter(response));
        }

        if (response.StatusCode is HttpStatusCode.BadRequest or
            HttpStatusCode.MethodNotAllowed or
            HttpStatusCode.NotImplemented or
            HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new DownloadException($"HTTP_{statusCode}", $"The server returned HTTP {statusCode}.");
        }

        var isPartial = response.StatusCode == HttpStatusCode.PartialContent;
        var finalUri = response.RequestMessage?.RequestUri ?? new Uri(job.Url);
        var contentLength = response.Content.Headers.ContentLength ?? 0;
        var totalBytes = contentLength;

        if (isPartial)
        {
            totalBytes = ParseTotalFromContentRange(response.Content.Headers.ContentRange?.ToString()) ?? contentLength;
        }

        return new HttpProbeResult
        {
            FinalUrl = finalUri.AbsoluteUri,
            FinalUri = finalUri,
            TotalBytes = totalBytes,
            SupportsRanges = isPartial && totalBytes > 0,
            ContentType = response.Content.Headers.ContentType?.ToString(),
            ETag = response.Headers.ETag?.ToString(),
            LastModified = response.Content.Headers.LastModified?.ToString("O", CultureInfo.InvariantCulture),
            ContentDisposition = response.Content.Headers.ContentDisposition?.ToString(),
            StatusCode = statusCode
        };
    }

    private static long? ParseTotalFromContentRange(string? contentRange)
    {
        if (string.IsNullOrWhiteSpace(contentRange))
        {
            return null;
        }

        var slash = contentRange.LastIndexOf('/');
        if (slash < 0 || slash == contentRange.Length - 1)
        {
            return null;
        }

        var totalText = contentRange[(slash + 1)..].Trim();
        return long.TryParse(totalText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var total) &&
               total >= 0
            ? total
            : null;
    }

    private async Task<ResumeMetadata?> TryLoadResumeMetadataAsync(
        string metaPath,
        string partPath,
        DownloadJob job,
        HttpProbeResult probe,
        CancellationToken cancellationToken)
    {
        if (job.ForceRestart || !File.Exists(metaPath) || !File.Exists(partPath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(metaPath, cancellationToken).ConfigureAwait(false);
            var metadata = JsonSerializer.Deserialize<ResumeMetadata>(json, ResumeMetadata.SerializerOptions);
            if (metadata is null || metadata.Segments.Count == 0)
            {
                return null;
            }

            if (!string.Equals(metadata.Url, job.Url, StringComparison.Ordinal))
            {
                return null;
            }

            if (!probe.SupportsRanges || probe.TotalBytes <= 0)
            {
                return null;
            }

            if (metadata.TotalBytes != probe.TotalBytes)
            {
                return null;
            }

            if (!string.Equals(metadata.ETag, probe.ETag, StringComparison.Ordinal) && metadata.ETag is not null)
            {
                return null;
            }

            if (!string.Equals(metadata.LastModified, probe.LastModified, StringComparison.Ordinal) &&
                metadata.LastModified is not null)
            {
                return null;
            }

            var totalDownloaded = metadata.Segments.Sum(segment => segment.Downloaded);
            if (totalDownloaded < 0 || totalDownloaded > metadata.TotalBytes)
            {
                return null;
            }

            if (metadata.Segments.Any(segment =>
                    segment.Start < 0 ||
                    segment.End >= metadata.TotalBytes ||
                    segment.End < segment.Start ||
                    segment.Downloaded > segment.Length))
            {
                return null;
            }

            return metadata;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static SegmentMetadata[] PlanSegments(HttpProbeResult probe, int requestedConnections)
    {
        var total = probe.TotalBytes;
        if (!probe.SupportsRanges || total <= 0)
        {
            return [new SegmentMetadata { Index = 0, Start = 0, End = total > 0 ? total - 1 : -1 }];
        }

        const long minimumSegmentSize = 2 * 1024 * 1024;
        var desired = Math.Clamp(requestedConnections, 1, 32);
        var maxUsefulSegments = (int)Math.Max(1, total / minimumSegmentSize);
        desired = Math.Min(desired, maxUsefulSegments);
        desired = Math.Min(desired, (int)Math.Min(total, int.MaxValue));

        var baseSize = total / desired;
        var remainder = total % desired;
        var segments = new SegmentMetadata[desired];
        long start = 0;

        for (var index = 0; index < desired; index++)
        {
            var size = baseSize + (index < remainder ? 1 : 0);
            var end = start + size - 1;
            segments[index] = new SegmentMetadata
            {
                Index = index,
                Start = start,
                End = end
            };
            start = end + 1;
        }

        return segments;
    }

    private async Task DownloadSegmentAsync(
        DownloadJob job,
        SegmentMetadata segment,
        int totalSegments,
        SafeFileHandle handle,
        ProgressCounter counter,
        TokenBucket? bucket,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        var attempt = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (segment.IsComplete)
            {
                return;
            }

            var start = segment.Start + segment.Downloaded;
            if (start > segment.End)
            {
                return;
            }

            var range = $"bytes={start}-{segment.End}";
            TimeSpan? retryAfter = null;

            try
            {
                using var request = CreateRequest(job, HttpMethod.Get, range);
                using var response = await _client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                var statusCode = (int)response.StatusCode;
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    if (segment.IsComplete)
                    {
                        return;
                    }

                    throw new DownloadException("HTTP_416", "The server rejected the requested byte range.");
                }

                if (statusCode is 408 or 429 || statusCode >= 500)
                {
                    retryAfter = GetRetryAfter(response);
                    throw new TransientProbeException(retryAfter);
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new DownloadException($"HTTP_{statusCode}", $"The server returned HTTP {statusCode}.");
                }

                if (response.StatusCode == HttpStatusCode.OK &&
                    (segment.Downloaded > 0 || totalSegments > 1))
                {
                    throw new DownloadException(
                        "RANGE_UNSUPPORTED",
                        "The server ignored the Range header while a segmented download was in progress.");
                }

                await using var stream = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);

                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    while (true)
                    {
                        var remaining = segment.Length > 0
                            ? segment.Length - segment.Downloaded
                            : BufferSize;
                        if (segment.Length > 0 && remaining <= 0)
                        {
                            break;
                        }

                        var toRead = (int)Math.Min(buffer.Length, remaining);
                        var read = await stream
                            .ReadAsync(buffer.AsMemory(0, toRead), cancellationToken)
                            .ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        if (bucket is not null)
                        {
                            await bucket.AcquireAsync(read, cancellationToken).ConfigureAwait(false);
                        }

                        var offset = segment.Start + segment.Downloaded;
                        await RandomAccess
                            .WriteAsync(handle, buffer.AsMemory(0, read), offset, cancellationToken)
                            .ConfigureAwait(false);

                        segment.AddDownloaded(read);
                        counter.Add(read);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                if (segment.Length > 0 && !segment.IsComplete)
                {
                    throw new IOException("The connection ended before the segment completed.");
                }

                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DownloadException)
            {
                throw;
            }
            catch (Exception exception) when (IsDiskFull(exception))
            {
                throw new DownloadException(ErrorCodes.DiskFull, "The disk is full.", exception);
            }
            catch (Exception exception) when (
                (exception is IOException or HttpRequestException or TransientProbeException) &&
                attempt < MaxRetries)
            {
                if (exception is TransientProbeException transient)
                {
                    retryAfter = transient.RetryAfter;
                }

                await DelayBeforeRetryAsync(attempt, retryAfter, cancellationToken).ConfigureAwait(false);
                attempt++;
            }
            catch (TransientProbeException exception)
            {
                throw new DownloadException(
                    "HTTP_TRANSIENT",
                    "The server kept returning a transient error.",
                    exception);
            }
        }
    }

    private async Task DownloadSingleStreamAsync(
        DownloadJob job,
        SegmentMetadata segment,
        SafeFileHandle handle,
        ProgressCounter counter,
        TokenBucket? bucket,
        CancellationToken cancellationToken)
    {
        var attempt = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan? retryAfter = null;

            try
            {
                using var request = CreateRequest(job, HttpMethod.Get, range: null);
                using var response = await _client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                var statusCode = (int)response.StatusCode;
                if (statusCode is 408 or 429 || statusCode >= 500)
                {
                    retryAfter = GetRetryAfter(response);
                    throw new TransientProbeException(retryAfter);
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new DownloadException($"HTTP_{statusCode}", $"The server returned HTTP {statusCode}.");
                }

                await using var stream = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);

                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    while (true)
                    {
                        var read = await stream
                            .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                            .ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        if (bucket is not null)
                        {
                            await bucket.AcquireAsync(read, cancellationToken).ConfigureAwait(false);
                        }

                        var offset = segment.Start + segment.Downloaded;
                        await RandomAccess
                            .WriteAsync(handle, buffer.AsMemory(0, read), offset, cancellationToken)
                            .ConfigureAwait(false);

                        segment.AddDownloaded(read);
                        counter.Add(read);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DownloadException)
            {
                throw;
            }
            catch (Exception exception) when (IsDiskFull(exception))
            {
                throw new DownloadException(ErrorCodes.DiskFull, "The disk is full.", exception);
            }
            catch (Exception exception) when (
                (exception is IOException or HttpRequestException or TransientProbeException) &&
                attempt < MaxRetries)
            {
                if (exception is TransientProbeException transient)
                {
                    retryAfter = transient.RetryAfter;
                }

                await DelayBeforeRetryAsync(attempt, retryAfter, cancellationToken).ConfigureAwait(false);
                attempt++;
            }
            catch (TransientProbeException exception)
            {
                throw new DownloadException(
                    "HTTP_TRANSIENT",
                    "The server kept returning a transient error.",
                    exception);
            }
        }
    }

    private async Task RunHeartbeatAsync(
        ResumeMetadata metadata,
        string metaPath,
        ProgressCounter counter,
        HttpProbeResult probe,
        SegmentMetadata[] segments,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var speedMeter = new SpeedMeter();
        var lastMetadataWrite = Stopwatch.GetTimestamp();

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var downloaded = counter.Value;
                var speed = speedMeter.AddSample(downloaded);
                var total = probe.TotalBytes;
                TimeSpan? eta = null;
                if (total > 0 && speed > 1)
                {
                    var seconds = Math.Max(0, (total - downloaded) / speed);
                    eta = TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.MaxValue.TotalSeconds));
                }

                try
                {
                    progress?.Report(new DownloadProgress
                    {
                        DownloadedBytes = downloaded,
                        TotalBytes = total,
                        SpeedBps = speed,
                        Eta = eta,
                        Stage = "Downloading",
                        ActiveSegments = segments.Count(segment => !segment.IsComplete)
                    });
                }
                catch (Exception exception)
                {
                    _logger.Warn($"Progress handler failed: {exception.Message}");
                }

                var elapsed = (Stopwatch.GetTimestamp() - lastMetadataWrite) / (double)Stopwatch.Frequency;
                if (elapsed >= 2)
                {
                    lastMetadataWrite = Stopwatch.GetTimestamp();
                    await FlushMetadataAsync(metaPath, metadata, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the download finishes or is stopped.
        }
    }

    private static async Task FlushMetadataAsync(
        string metaPath,
        ResumeMetadata metadata,
        CancellationToken cancellationToken)
    {
        try
        {
            metadata.UpdatedAt = DateTimeOffset.UtcNow;
            var json = JsonSerializer.Serialize(metadata, ResumeMetadata.SerializerOptions);
            await AtomicFile.WriteAllTextAsync(metaPath, json, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Metadata is best-effort; the partial file remains usable for a retry.
        }
        catch (UnauthorizedAccessException)
        {
            // Metadata is best-effort.
        }
    }

    private static async Task AwaitHeartbeatAsync(Task? heartbeat)
    {
        if (heartbeat is null)
        {
            return;
        }

        try
        {
            await heartbeat.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }
    }

    private HttpRequestMessage CreateRequest(DownloadJob job, HttpMethod method, string? range)
    {
        var request = new HttpRequestMessage(method, job.Url);
        var userAgent = job.UserAgent ?? _defaultUserAgent;
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }

        if (!string.IsNullOrWhiteSpace(job.Referer))
        {
            request.Headers.TryAddWithoutValidation("Referer", job.Referer);
        }

        if (!string.IsNullOrWhiteSpace(job.Cookies))
        {
            request.Headers.TryAddWithoutValidation("Cookie", job.Cookies);
        }

        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");

        if (range is not null)
        {
            request.Headers.TryAddWithoutValidation("Range", range);
        }

        return request;
    }

    private string ResolveFileName(DownloadJob job, HttpProbeResult probe)
    {
        if (!string.IsNullOrWhiteSpace(job.FileName))
        {
            return job.FileName;
        }

        var dispositionName = ContentDispositionParser.ExtractFileName(probe.ContentDisposition);
        if (!string.IsNullOrWhiteSpace(dispositionName))
        {
            return dispositionName;
        }

        return FilenamePolicy.DeriveFromUrl(
            probe.FinalUrl,
            CategoryResolver.DefaultExtensionFor(probe.ContentType ?? string.Empty));
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    private static Task DelayBeforeRetryAsync(
        int attempt,
        TimeSpan? retryAfter,
        CancellationToken cancellationToken)
    {
        var backoffSeconds = Math.Min(10, Math.Pow(2, attempt) * 0.5);
        var jitter = Random.Shared.NextDouble() * 0.25;
        var delay = retryAfter ?? TimeSpan.FromSeconds(backoffSeconds + jitter);
        if (delay > TimeSpan.FromSeconds(30))
        {
            delay = TimeSpan.FromSeconds(30);
        }

        return Task.Delay(delay, cancellationToken);
    }

    private static bool IsDiskFull(Exception exception) =>
        exception.HResult is 112 or unchecked((int)0x80070070);

    private static (string Code, string Message) MapException(Exception exception)
    {
        return exception switch
        {
            UnauthorizedAccessException => ("ACCESS_DENIED", exception.Message),
            HttpRequestException => ("NETWORK_ERROR", exception.Message),
            _ => (ErrorCodes.Internal, exception.Message)
        };
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }

    private static DownloadOutcome Failure(
        string code,
        string message,
        string? filePath = null,
        string? finalUrl = null,
        long totalBytes = 0,
        long downloadedBytes = 0) =>
        new()
        {
            Success = false,
            ErrorCode = code,
            ErrorMessage = message,
            FilePath = filePath,
            FinalUrl = finalUrl,
            TotalBytes = totalBytes,
            DownloadedBytes = downloadedBytes
        };

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private sealed class ProgressCounter(long initial)
    {
        private long _value = initial;

        public long Value => Interlocked.Read(ref _value);

        public void Add(long bytes) => Interlocked.Add(ref _value, bytes);
    }

    private sealed class TransientProbeException(TimeSpan? retryAfter) : Exception
    {
        public TimeSpan? RetryAfter { get; } = retryAfter;
    }
}
