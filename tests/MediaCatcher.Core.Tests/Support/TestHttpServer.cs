using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MediaCatcher.Core.Tests.Support;

public sealed record TestHttpRequest(string Method, string PathAndQuery, string? Range);

public sealed class TestHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _acceptLoop;

    public TestHttpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        Port = endpoint.Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
    }

    public int Port { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public long FileSize { get; set; } = 6 * 1024 * 1024;

    public int DelayMillisecondsPerChunk { get; set; }

    public int ChunkSize { get; set; } = 64 * 1024;

    public int FailWith429Count { get; set; }

    public string ETag { get; set; } = "\"v1\"";

    public int ContentSeed { get; set; }

    public bool SupportsRanges { get; set; } = true;

    public ConcurrentQueue<TestHttpRequest> Requests { get; } = new();

    public byte ByteAt(long index) =>
        (byte)(((index * 31) + 7 + ContentSeed) % 251);

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = Task.Run(() => HandleClientAsync(client, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                await using var stream = client.GetStream();
                var headerText = await ReadHeadersAsync(stream, cancellationToken).ConfigureAwait(false);
                if (headerText is null)
                {
                    return;
                }

                var lines = headerText.Split("\r\n", StringSplitOptions.None);
                var requestLine = lines[0].Split(' ');
                if (requestLine.Length < 2)
                {
                    return;
                }

                var method = requestLine[0];
                var target = requestLine[1];
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    var separator = line.IndexOf(':');
                    if (separator <= 0)
                    {
                        continue;
                    }

                    headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
                }

                var path = target.Split('?')[0];
                headers.TryGetValue("Range", out var rangeHeader);
                Requests.Enqueue(new TestHttpRequest(method, target, rangeHeader));

                if (path.Equals("/redirect", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(
                        stream,
                        "302 Found",
                        "Location: /file\r\n",
                        0,
                        0,
                        method,
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (path.Equals("/429", StringComparison.OrdinalIgnoreCase) ||
                    (path.Equals("/file", StringComparison.OrdinalIgnoreCase) && FailWith429Count > 0))
                {
                    if (FailWith429Count > 0)
                    {
                        FailWith429Count--;
                        await WriteResponseAsync(
                            stream,
                            "429 Too Many Requests",
                            "Retry-After: 0\r\n",
                            0,
                            0,
                            method,
                            cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }

                if (path.Equals("/404", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(
                        stream,
                        "404 Not Found",
                        "",
                        0,
                        0,
                        method,
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                var supportsRanges = SupportsRanges && !path.Equals("/norange", StringComparison.OrdinalIgnoreCase);
                var delay = path.Equals("/slow", StringComparison.OrdinalIgnoreCase)
                    ? Math.Max(1, DelayMillisecondsPerChunk)
                    : DelayMillisecondsPerChunk;

                long start = 0;
                long end = FileSize - 1;
                var partial = false;

                if (supportsRanges && !string.IsNullOrWhiteSpace(rangeHeader) &&
                    TryParseRange(rangeHeader, FileSize, out var requestedStart, out var requestedEnd))
                {
                    start = requestedStart;
                    end = requestedEnd;
                    partial = true;
                }

                var bodyLength = Math.Max(0, end - start + 1);
                var extraHeaders = new StringBuilder();
                extraHeaders.Append("Accept-Ranges: ").Append(supportsRanges ? "bytes" : "none").Append("\r\n");
                extraHeaders.Append("ETag: ").Append(ETag).Append("\r\n");
                extraHeaders.Append("Last-Modified: Mon, 01 Jan 2024 00:00:00 GMT\r\n");
                extraHeaders.Append("Content-Type: application/octet-stream\r\n");

                if (path.Equals("/unicode", StringComparison.OrdinalIgnoreCase))
                {
                    extraHeaders.Append(
                        "Content-Disposition: attachment; filename*=UTF-8''%E0%B8%97%E0%B8%94%E0%B8%AA%E0%B8%AD%E0%B8%9A.bin\r\n");
                }
                else if (path.Equals("/crlf", StringComparison.OrdinalIgnoreCase))
                {
                    extraHeaders.Append("Content-Disposition: attachment; filename=\"../evil\\r\\nX-Test: 1.txt\"\r\n");
                }

                if (partial)
                {
                    extraHeaders.Append("Content-Range: bytes ")
                        .Append(start.ToString(CultureInfo.InvariantCulture))
                        .Append('-')
                        .Append(end.ToString(CultureInfo.InvariantCulture))
                        .Append('/')
                        .Append(FileSize.ToString(CultureInfo.InvariantCulture))
                        .Append("\r\n");
                }

                await WriteResponseAsync(
                    stream,
                    partial ? "206 Partial Content" : "200 OK",
                    extraHeaders.ToString(),
                    start,
                    bodyLength,
                    method,
                    cancellationToken,
                    delay).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Client disconnected.
            }
            catch (OperationCanceledException)
            {
                // Server shutting down.
            }
            catch (ObjectDisposedException)
            {
                // Server shutting down.
            }
        }
    }

    private async Task WriteResponseAsync(
        Stream stream,
        string status,
        string extraHeaders,
        long bodyStart,
        long bodyLength,
        string method,
        CancellationToken cancellationToken,
        int delayMilliseconds = 0)
    {
        var header = new StringBuilder();
        header.Append("HTTP/1.1 ").Append(status).Append("\r\n");
        header.Append("Connection: close\r\n");
        header.Append("Content-Length: ").Append(bodyLength.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        header.Append(extraHeaders);
        header.Append("\r\n");

        var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);

        if (method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var buffer = new byte[Math.Min(Math.Max(1, ChunkSize), 256 * 1024)];
        var position = bodyStart;
        var remaining = bodyLength;
        while (remaining > 0)
        {
            var count = (int)Math.Min(buffer.Length, remaining);
            for (var index = 0; index < count; index++)
            {
                buffer[index] = ByteAt(position + index);
            }

            await stream.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            position += count;
            remaining -= count;

            if (delayMilliseconds > 0)
            {
                await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool TryParseRange(string header, long size, out long start, out long end)
    {
        start = 0;
        end = size - 1;
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var spec = header[6..].Split(',')[0].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return false;
        }

        var startText = spec[..dash].Trim();
        var endText = spec[(dash + 1)..].Trim();
        if (startText.Length == 0)
        {
            return false;
        }

        if (!long.TryParse(startText, NumberStyles.Integer, CultureInfo.InvariantCulture, out start))
        {
            return false;
        }

        if (endText.Length > 0)
        {
            if (!long.TryParse(endText, NumberStyles.Integer, CultureInfo.InvariantCulture, out end))
            {
                return false;
            }
        }

        if (start < 0 || start >= size)
        {
            return false;
        }

        end = Math.Min(end, size - 1);
        return end >= start;
    }

    private static async Task<string?> ReadHeadersAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var builder = new StringBuilder();
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return builder.Length > 0 ? builder.ToString() : null;
            }

            builder.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (builder.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                return builder.ToString();
            }

            if (builder.Length > 65536)
            {
                return null;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        _shutdown.Dispose();
    }
}
