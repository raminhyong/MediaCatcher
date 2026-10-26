using MediaCatcher.Contracts;
using MediaCatcher.Core.Logging;
using MediaCatcher.Core.Models;
using MediaCatcher.Core.Queue;
using MediaCatcher.Core.Storage;
using MediaCatcher.Core.Utils;
using MediaCatcher.Core.YtDlp;

namespace MediaCatcher.Core.Ipc;

public sealed class IpcRequestHandler
{
    private static readonly TimeSpan AnalyzeTimeout = TimeSpan.FromSeconds(115);

    private readonly QueueManager _queue;
    private readonly SettingsService _settings;
    private readonly Func<Task> _activateWindow;
    private readonly IAppLogger _logger;
    private readonly string _appVersion;

    public IpcRequestHandler(
        QueueManager queue,
        SettingsService settings,
        Func<Task> activateWindow,
        IAppLogger? logger = null,
        string appVersion = "0.1.0")
    {
        _queue = queue;
        _settings = settings;
        _activateWindow = activateWindow;
        _logger = logger ?? NullAppLogger.Instance;
        _appVersion = appVersion;
    }

    public async Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.Info($"IPC {request.Action} ({request.Source ?? "unknown"})");

            return request.Action switch
            {
                IpcActions.Ping => new IpcResponse
                {
                    Id = request.Id,
                    Ok = true,
                    Ping = new PingInfo
                    {
                        Protocol = Protocol.Version,
                        AppVersion = _appVersion
                    }
                },
                IpcActions.GetStatus => new IpcResponse
                {
                    Id = request.Id,
                    Ok = true,
                    Status = _queue.GetStatus()
                },
                IpcActions.OpenApp or IpcActions.Activate => await HandleActivateAsync(request)
                    .ConfigureAwait(false),
                IpcActions.AddDownload => await HandleAddDownloadAsync(request, cancellationToken)
                    .ConfigureAwait(false),
                IpcActions.AddBatch => await HandleAddBatchAsync(request, cancellationToken)
                    .ConfigureAwait(false),
                IpcActions.Analyze => await HandleAnalyzeAsync(request, cancellationToken)
                    .ConfigureAwait(false),
                _ => IpcResponse.Failure(
                    request.Id,
                    ErrorCodes.InvalidRequest,
                    $"Unknown action '{request.Action}'.")
            };
        }
        catch (DownloadException exception)
        {
            return IpcResponse.Failure(request.Id, exception.Code, exception.Message);
        }
        catch (OperationCanceledException)
        {
            return IpcResponse.Failure(request.Id, ErrorCodes.HostTimeout, "The request was canceled.");
        }
        catch (Exception exception)
        {
            _logger.Error($"IPC {request.Action} failed.", exception);
            return IpcResponse.Failure(request.Id, ErrorCodes.Internal, exception.Message);
        }
    }

    private async Task<IpcResponse> HandleActivateAsync(IpcRequest request)
    {
        await _activateWindow().ConfigureAwait(false);
        return new IpcResponse { Id = request.Id, Ok = true };
    }

    private async Task<IpcResponse> HandleAddDownloadAsync(
        IpcRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Download is null || string.IsNullOrWhiteSpace(request.Download.Url))
        {
            return IpcResponse.Failure(request.Id, ErrorCodes.InvalidRequest, "A download URL is required.");
        }

        var result = await _queue
            .AddAsync(Map(request.Download, request.Source), cancellationToken)
            .ConfigureAwait(false);

        if (result.IsDuplicate)
        {
            return new IpcResponse
            {
                Id = request.Id,
                Ok = false,
                Code = ErrorCodes.Duplicate,
                Message = "This URL is already in the download queue.",
                DownloadId = result.Item.Id
            };
        }

        return new IpcResponse
        {
            Id = request.Id,
            Ok = true,
            DownloadId = result.Item.Id
        };
    }

    private async Task<IpcResponse> HandleAddBatchAsync(
        IpcRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Items is null || request.Items.Count == 0)
        {
            return IpcResponse.Failure(request.Id, ErrorCodes.InvalidRequest, "At least one item is required.");
        }

        var mapped = request.Items
            .Where(item => item is not null)
            .Take(500)
            .Select(item => Map(item, request.Source))
            .ToList();

        var result = await _queue.AddBatchAsync(mapped, cancellationToken).ConfigureAwait(false);
        return new IpcResponse
        {
            Id = request.Id,
            Ok = true,
            Accepted = result.Accepted,
            Skipped = result.Skipped
        };
    }

    private async Task<IpcResponse> HandleAnalyzeAsync(
        IpcRequest request,
        CancellationToken cancellationToken)
    {
        var url = request.Analyze?.Url;
        if (string.IsNullOrWhiteSpace(url) || !UrlPolicy.IsSupported(url))
        {
            return IpcResponse.Failure(request.Id, ErrorCodes.UnsupportedUrl, "A valid http/https URL is required.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AnalyzeTimeout);

        var locator = ToolLocator.FromSettings(_settings.Current, _logger);
        var engine = new YtDlpEngine(locator, _logger);

        var analysis = await engine.AnalyzeAsync(url, timeout.Token).ConfigureAwait(false);
        return new IpcResponse
        {
            Id = request.Id,
            Ok = true,
            Analysis = analysis
        };
    }

    private static NewDownload Map(AddDownloadRequest request, string? source) =>
        new()
        {
            Url = request.Url ?? string.Empty,
            FileName = request.FileName,
            Referer = request.Referer,
            Cookies = request.Cookies,
            UserAgent = request.UserAgent,
            Category = request.Category ?? request.Kind,
            Engine = request.Engine,
            Format = request.Format,
            Source = source,
            Takeover = request.Takeover ?? false
        };
}
