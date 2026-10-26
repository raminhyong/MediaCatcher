using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;

namespace MediaCatcher.Contracts;

public sealed class NamedPipeIpcClient
{
    private readonly string _pipeName;

    public NamedPipeIpcClient(string? pipeName = null)
    {
        _pipeName = pipeName ?? Protocol.PipeNameFor(UserIdentity.ResolveCurrentUserSid());
    }

    public string PipeName => _pipeName;

    public async Task<IpcResponse> SendAsync(
        IpcRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            await pipe.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
            pipe.ReadMode = PipeTransmissionMode.Byte;

            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true
            };
            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, 4096, leaveOpen: true);

            await writer.WriteLineAsync(IpcJson.Serialize(request)).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);

            var line = await reader.ReadLineAsync(timeoutSource.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
            {
                return IpcResponse.Failure(request.Id, ErrorCodes.HostTimeout, "The app closed the IPC connection before replying.");
            }

            var response = IpcJson.Deserialize<IpcResponse>(line);
            return response ?? IpcResponse.Failure(request.Id, ErrorCodes.Internal, "The app returned an invalid response.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return IpcResponse.Failure(request.Id, ErrorCodes.HostTimeout, "The app did not reply in time.");
        }
        catch (TimeoutException)
        {
            return IpcResponse.Failure(request.Id, ErrorCodes.HostTimeout, "The app did not reply in time.");
        }
        catch (IOException exception)
        {
            return IpcResponse.Failure(request.Id, ErrorCodes.AppNotStarted, exception.Message);
        }
    }
}

public sealed class NamedPipeIpcServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly Func<IpcRequest, CancellationToken, Task<IpcResponse>> _handler;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<int, Task> _connections = new();
    private int _connectionSequence;
    private Task? _acceptLoop;

    public NamedPipeIpcServer(
        Func<IpcRequest, CancellationToken, Task<IpcResponse>> handler,
        string? pipeName = null)
    {
        _handler = handler;
        _pipeName = pipeName ?? Protocol.PipeNameFor(UserIdentity.ResolveCurrentUserSid());
    }

    public string PipeName => _pipeName;

    public void Start()
    {
        _acceptLoop ??= Task.Run(() => AcceptLoopAsync(_shutdown.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    8,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                var connectionId = Interlocked.Increment(ref _connectionSequence);
                var connection = HandleConnectionAsync(server, cancellationToken);
                server = null;
                _connections[connectionId] = connection;
                _ = connection.ContinueWith(
                    completedTask => _connections.TryRemove(connectionId, out var _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (server is not null)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        await using (server.ConfigureAwait(false))
        {
            try
            {
                using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true)
                {
                    AutoFlush = true
                };

                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line))
                {
                    return;
                }

                IpcResponse response;
                IpcRequest? request = null;
                try
                {
                    request = IpcJson.Deserialize<IpcRequest>(line);
                    if (request is null)
                    {
                        response = IpcResponse.Failure(string.Empty, ErrorCodes.InvalidRequest, "Empty IPC request.");
                    }
                    else
                    {
                        response = await _handler(request, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    response = IpcResponse.Failure(
                        request?.Id ?? string.Empty,
                        ErrorCodes.Internal,
                        exception.Message);
                }

                await writer.WriteLineAsync(IpcJson.Serialize(response)).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // App shutting down.
            }
            catch (IOException)
            {
                // Client vanished.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_acceptLoop is not null)
            {
                await _acceptLoop.ConfigureAwait(false);
            }

            await Task.WhenAll(_connections.Values).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        finally
        {
            _shutdown.Dispose();
        }
    }
}
