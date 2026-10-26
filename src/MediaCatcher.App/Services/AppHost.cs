using MediaCatcher.App.Localization;
using MediaCatcher.App.Views;
using MediaCatcher.Contracts;
using MediaCatcher.Core.Ipc;
using MediaCatcher.Core.Logging;
using MediaCatcher.Core.Queue;
using MediaCatcher.Core.Storage;

namespace MediaCatcher.App.Services;

public sealed class AppHost : IAsyncDisposable
{
    private readonly IAppLogger _logger;
    private NamedPipeIpcServer? _server;
    private bool _disposed;

    public AppHost(
        AppPaths paths,
        SettingsService settings,
        DownloadStore store,
        QueueManager queue,
        IAppLogger logger)
    {
        Paths = paths;
        Settings = settings;
        Store = store;
        Queue = queue;
        _logger = logger;
    }

    public AppPaths Paths { get; }

    public SettingsService Settings { get; }

    public DownloadStore Store { get; }

    public QueueManager Queue { get; }

    public MainWindow? MainWindow { get; set; }

    public event EventHandler? ActivationRequested;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Paths.EnsureDirectories();
        var loaded = await Settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        Strings.ApplyCulture(loaded.Language);
        await Queue.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _logger.Info($"Media Catcher {AppInfo.Version} initialized. Root={loaded.DownloadRoot}");
    }

    public void StartPipeServer()
    {
        if (_server is not null)
        {
            return;
        }

        var handler = new IpcRequestHandler(
            Queue,
            Settings,
            ActivateWindowAsync,
            _logger,
            AppInfo.Version);

        _server = new NamedPipeIpcServer(handler.HandleAsync);
        _server.Start();
        _logger.Info($"IPC pipe listening on {_server.PipeName}");
    }

    public Task ActivateWindowAsync()
    {
        ActivationRequested?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public async Task ShutdownAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _logger.Info("Shutting down.");

        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            _server = null;
        }

        await Queue.ShutdownAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await ShutdownAsync().ConfigureAwait(false);
}
