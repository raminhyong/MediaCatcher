using System.Diagnostics;
using System.Globalization;
using MediaCatcher.Contracts;
using MediaCatcher.Core.Document;
using MediaCatcher.Core.Http;
using MediaCatcher.Core.Logging;
using MediaCatcher.Core.Models;
using MediaCatcher.Core.Routing;
using MediaCatcher.Core.Storage;
using MediaCatcher.Core.Utils;
using MediaCatcher.Core.YtDlp;

namespace MediaCatcher.Core.Queue;

public sealed record NewDownload
{
    public required string Url { get; init; }

    public string? FileName { get; init; }

    public string? Referer { get; init; }

    public string? Cookies { get; init; }

    public string? UserAgent { get; init; }

    public string? Category { get; init; }

    public string? Engine { get; init; }

    public string? Format { get; init; }

    public string? Source { get; init; }

    public bool Takeover { get; init; }

    public int Priority { get; init; }
}

public sealed record EnqueueResult(DownloadItem Item, bool IsDuplicate);

public sealed record BatchEnqueueResult(int Accepted, int Skipped, IReadOnlyList<DownloadItem> Items);

public sealed class QueueManager : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DownloadItem> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RunningDownload> _running = new(StringComparer.Ordinal);
    private readonly HashSet<string> _removedIds = new(StringComparer.Ordinal);
    private readonly SettingsService _settings;
    private readonly DownloadStore _store;
    private readonly IAppLogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _appVersion;
    private TokenBucket? _globalBucket;
    private long _globalBucketLimit;
    private Task? _scheduler;
    private bool _drainedRaised;
    private bool _initialized;

    public QueueManager(
        SettingsService settings,
        DownloadStore store,
        IAppLogger? logger = null,
        string appVersion = "0.1.0")
    {
        _settings = settings;
        _store = store;
        _logger = logger ?? NullAppLogger.Instance;
        _appVersion = appVersion;
    }

    public event EventHandler<DownloadItem>? ItemUpdated;

    public event EventHandler? QueueChanged;

    public event EventHandler? QueueDrained;

    public bool IsInitialized => _initialized;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var stored = await _store.GetRecentAsync(500, cancellationToken).ConfigureAwait(false);
        var settings = _settings.Current;
        var autoResume = settings.AutoResumeOnLaunch;

        foreach (var item in stored)
        {
            if (item.State is DownloadState.Probing or DownloadState.Downloading or DownloadState.Merging)
            {
                item.State = autoResume ? DownloadState.Queued : DownloadState.Paused;
                item.SpeedBps = 0;
                await _store.UpsertAsync(item, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            lock (_gate)
            {
                _items[item.Id] = item;
            }
        }

        _initialized = true;
        _scheduler = Task.Run(() => SchedulerLoopAsync(_lifetime.Token));
        RaiseQueueChanged();

        if (!autoResume)
        {
            _logger.Info("Queue restored. Auto-resume is disabled.");
        }
    }

    public async Task<EnqueueResult> AddAsync(NewDownload request, CancellationToken cancellationToken = default)
    {
        if (!UrlPolicy.TryNormalize(request.Url, out var uri, out var error))
        {
            throw new DownloadException(ErrorCodes.UnsupportedUrl, error ?? "Unsupported URL.");
        }

        var settings = _settings.Current;
        var kind = EngineRouter.Choose(uri.AbsoluteUri, explicitEngine: request.Engine);
        var fallbackExtension = kind switch
        {
            DownloadEngineKind.YtDlp => "mp4",
            DownloadEngineKind.Document => "pdf",
            _ => "bin"
        };
        var fileName = string.IsNullOrWhiteSpace(request.FileName)
            ? FilenamePolicy.DeriveFromUrl(uri.AbsoluteUri, fallbackExtension)
            : FilenamePolicy.Sanitize(request.FileName, fallbackExtension);

        var category = ResolveCategory(request.Category, fileName, kind);
        var directory = ResolveDirectory(settings, category);

        var duplicate = await FindExistingAsync(uri.AbsoluteUri, cancellationToken).ConfigureAwait(false);
        if (duplicate is not null)
        {
            return new EnqueueResult(duplicate, IsDuplicate: true);
        }

        var item = new DownloadItem
        {
            Url = uri.AbsoluteUri,
            FileName = fileName,
            Directory = directory,
            Category = category,
            Kind = kind,
            State = DownloadState.Queued,
            Connections = settings.Connections,
            Referer = string.IsNullOrWhiteSpace(request.Referer) ? null : request.Referer,
            UserAgent = string.IsNullOrWhiteSpace(request.UserAgent) ? settings.UserAgent : request.UserAgent,
            Engine = request.Engine,
            YtdlpFormat = request.Format,
            Source = request.Source,
            Takeover = request.Takeover,
            Priority = request.Priority,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var protectedCookies = CookieVault.Protect(request.Cookies);
        await _store
            .UpsertAsync(item, protectedCookies, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        lock (_gate)
        {
            _items[item.Id] = item;
        }

        _logger.Info($"Queued {item.Url} as {item.Kind} in {item.Directory}");
        Notify(item);
        RaiseQueueChanged();
        return new EnqueueResult(item.Clone(), IsDuplicate: false);
    }

    public async Task<BatchEnqueueResult> AddBatchAsync(
        IReadOnlyList<NewDownload> requests,
        CancellationToken cancellationToken = default)
    {
        var items = new List<DownloadItem>();
        var accepted = 0;
        var skipped = 0;

        foreach (var request in requests.Take(500))
        {
            try
            {
                var result = await AddAsync(request, cancellationToken).ConfigureAwait(false);
                if (result.IsDuplicate)
                {
                    skipped++;
                }
                else
                {
                    accepted++;
                    items.Add(result.Item);
                }
            }
            catch (DownloadException)
            {
                skipped++;
            }
        }

        return new BatchEnqueueResult(accepted, skipped, items);
    }

    public Task PauseAsync(string id, CancellationToken cancellationToken = default) =>
        SetPausedAsync(id, pauseRunning: true, cancellationToken);

    public async Task PauseAllAsync(CancellationToken cancellationToken = default)
    {
        List<RunningDownload> running;
        var changed = new List<DownloadItem>();
        lock (_gate)
        {
            running = _running.Values.ToList();
            foreach (var item in _items.Values)
            {
                if (item.State is DownloadState.Queued or DownloadState.WaitingForSchedule)
                {
                    item.State = DownloadState.Paused;
                    changed.Add(item);
                }
            }
        }

        foreach (var run in running)
        {
            run.PauseRequested = true;
            run.Cancellation.Cancel();
        }

        foreach (var item in changed)
        {
            await PersistAsync(item, cancellationToken).ConfigureAwait(false);
            Notify(item);
        }

        RaiseQueueChanged();
    }

    public async Task ResumeAsync(string id, CancellationToken cancellationToken = default)
    {
        DownloadItem? item;
        lock (_gate)
        {
            _items.TryGetValue(id, out item);
            if (item is null || item.State is DownloadState.Completed or DownloadState.Downloading or DownloadState.Probing)
            {
                return;
            }

            item.State = DownloadState.Queued;
            item.Error = null;
            item.CompletedAt = null;
        }

        await PersistAsync(item, cancellationToken).ConfigureAwait(false);
        Notify(item);
        RaiseQueueChanged();
    }

    public async Task ResumeAllAsync(CancellationToken cancellationToken = default)
    {
        var changed = new List<DownloadItem>();
        lock (_gate)
        {
            foreach (var item in _items.Values)
            {
                if (item.State is DownloadState.Paused)
                {
                    item.State = DownloadState.Queued;
                    item.Error = null;
                    changed.Add(item);
                }
            }
        }

        foreach (var item in changed)
        {
            await PersistAsync(item, cancellationToken).ConfigureAwait(false);
            Notify(item);
        }

        RaiseQueueChanged();
    }

    public async Task CancelAsync(string id, CancellationToken cancellationToken = default)
    {
        DownloadItem? item;
        RunningDownload? running = null;
        lock (_gate)
        {
            if (!_items.TryGetValue(id, out item))
            {
                return;
            }

            if (_running.TryGetValue(id, out running))
            {
                running.CancelRequested = true;
            }
            else if (item.State is not (DownloadState.Completed or DownloadState.Canceled))
            {
                item.State = DownloadState.Canceled;
                item.SpeedBps = 0;
            }
        }

        if (running is not null)
        {
            running.Cancellation.Cancel();
            return;
        }

        await _store.ClearCookiesAsync(id, cancellationToken).ConfigureAwait(false);
        CleanupPartial(item);
        await PersistAsync(item, cancellationToken).ConfigureAwait(false);
        Notify(item);
        RaiseQueueChanged();
    }

    public async Task RetryAsync(string id, CancellationToken cancellationToken = default)
    {
        await ResumeAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        RunningDownload? running;
        lock (_gate)
        {
            _removedIds.Add(id);
            _running.TryGetValue(id, out running);
            if (running is not null)
            {
                running.CancelRequested = true;
            }
        }

        if (running is not null)
        {
            running.Cancellation.Cancel();
            for (var index = 0; index < 50; index++)
            {
                lock (_gate)
                {
                    if (!_running.ContainsKey(id))
                    {
                        break;
                    }
                }

                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }

        lock (_gate)
        {
            _items.Remove(id);
        }

        await _store.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        NotifyDeleted(id);
        RaiseQueueChanged();
    }

    public async Task ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var terminal = _items.Values
                .Where(item => item.State is DownloadState.Completed or DownloadState.Failed or DownloadState.Canceled)
                .Select(item => item.Id)
                .ToList();
            foreach (var id in terminal)
            {
                _items.Remove(id);
            }
        }

        await _store.ClearHistoryAsync(cancellationToken).ConfigureAwait(false);
        RaiseQueueChanged();
    }

    public List<DownloadItem> GetItems()
    {
        lock (_gate)
        {
            return _items.Values
                .Select(item => item.Clone())
                .OrderByDescending(item => item.CreatedAt)
                .ToList();
        }
    }

    public StatusInfo GetStatus()
    {
        lock (_gate)
        {
            var active = _items.Values.Count(item =>
                item.State is DownloadState.Probing or DownloadState.Downloading or DownloadState.Merging);
            var queued = _items.Values.Count(item =>
                item.State is DownloadState.Queued or DownloadState.WaitingForSchedule);
            var speed = _items.Values
                .Where(item => item.State is DownloadState.Probing or DownloadState.Downloading or DownloadState.Merging)
                .Sum(item => item.SpeedBps);

            return new StatusInfo
            {
                Running = active > 0,
                ActiveCount = active,
                QueuedCount = queued,
                SpeedBps = (long)Math.Max(0, speed),
                AppVersion = _appVersion
            };
        }
    }

    private async Task SetPausedAsync(
        string id,
        bool pauseRunning,
        CancellationToken cancellationToken)
    {
        DownloadItem? item;
        RunningDownload? running = null;
        lock (_gate)
        {
            if (!_items.TryGetValue(id, out item))
            {
                return;
            }

            if (pauseRunning && _running.TryGetValue(id, out running))
            {
                running.PauseRequested = true;
            }
            else if (item.State is DownloadState.Queued or DownloadState.WaitingForSchedule)
            {
                item.State = DownloadState.Paused;
                item.SpeedBps = 0;
            }
            else
            {
                return;
            }
        }

        if (running is not null)
        {
            running.Cancellation.Cancel();
            return;
        }

        await PersistAsync(item, cancellationToken).ConfigureAwait(false);
        Notify(item);
        RaiseQueueChanged();
    }

    private async Task<DownloadItem?> FindExistingAsync(string url, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var active = _items.Values.FirstOrDefault(item =>
                string.Equals(item.Url, url, StringComparison.Ordinal) &&
                item.State is not (DownloadState.Failed or DownloadState.Canceled or DownloadState.Completed));
            if (active is not null)
            {
                return active.Clone();
            }
        }

        var stored = await _store.FindDuplicateAsync(url, null, 0, cancellationToken).ConfigureAwait(false);
        return stored;
    }

    private async Task SchedulerLoopAsync(CancellationToken cancellationToken)
    {
        var lastProgressPersist = Stopwatch.GetTimestamp();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await EnforceScheduleAsync(cancellationToken).ConfigureAwait(false);
                await DispatchAsync(cancellationToken).ConfigureAwait(false);

                var elapsed = (Stopwatch.GetTimestamp() - lastProgressPersist) / (double)Stopwatch.Frequency;
                if (elapsed >= 2)
                {
                    lastProgressPersist = Stopwatch.GetTimestamp();
                    await PersistRunningProgressAsync(cancellationToken).ConfigureAwait(false);
                }

                UpdateDrainedState();
                await Task.Delay(400, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.Error("Queue scheduler error.", exception);
                try
                {
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task EnforceScheduleAsync(CancellationToken cancellationToken)
    {
        var settings = _settings.Current;
        var withinSchedule = IsWithinSchedule(settings, DateTime.Now);
        var changed = new List<DownloadItem>();
        List<RunningDownload> toPause = [];

        lock (_gate)
        {
            foreach (var item in _items.Values)
            {
                if (withinSchedule && item.State == DownloadState.WaitingForSchedule)
                {
                    item.State = DownloadState.Queued;
                    changed.Add(item);
                }
                else if (!withinSchedule && item.State == DownloadState.Queued)
                {
                    item.State = DownloadState.WaitingForSchedule;
                    changed.Add(item);
                }
            }

            if (!withinSchedule)
            {
                toPause = _running.Values.Where(run => !run.PauseRequested).ToList();
            }
        }

        foreach (var item in changed)
        {
            await PersistAsync(item, cancellationToken).ConfigureAwait(false);
            Notify(item);
        }

        foreach (var run in toPause)
        {
            run.PauseRequested = true;
            run.Cancellation.Cancel();
        }
    }

    private async Task DispatchAsync(CancellationToken cancellationToken)
    {
        var settings = _settings.Current;
        if (!IsWithinSchedule(settings, DateTime.Now))
        {
            return;
        }

        var toStart = new List<(DownloadItem Item, RunningDownload Running)>();
        lock (_gate)
        {
            if (_running.Count >= settings.MaxConcurrentDownloads)
            {
                return;
            }

            var hostCounts = _running.Values
                .GroupBy(run => run.Host, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

            var candidates = _items.Values
                .Where(item => item.State == DownloadState.Queued)
                .OrderByDescending(item => item.Priority)
                .ThenBy(item => item.CreatedAt)
                .ToList();

            foreach (var item in candidates)
            {
                if (_running.Count + toStart.Count >= settings.MaxConcurrentDownloads)
                {
                    break;
                }

                var host = item.Host;
                hostCounts.TryGetValue(host, out var activeForHost);
                if (activeForHost >= settings.PerHostLimit)
                {
                    continue;
                }

                hostCounts[host] = activeForHost + 1;
                var running = new RunningDownload(
                    item.Id,
                    host,
                    CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
                _running[item.Id] = running;
                item.State = DownloadState.Probing;
                toStart.Add((item, running));
            }
        }

        foreach (var (item, running) in toStart)
        {
            await PersistAsync(item, cancellationToken).ConfigureAwait(false);
            Notify(item);
            _ = RunItemAsync(item, running);
        }

        if (toStart.Count > 0)
        {
            RaiseQueueChanged();
        }
    }

    private async Task RunItemAsync(DownloadItem item, RunningDownload running)
    {
        var cancellationToken = running.Cancellation.Token;
        var cookies = (string?)null;

        try
        {
            item.StartedAt ??= DateTimeOffset.UtcNow;
            item.State = DownloadState.Downloading;
            item.Error = null;
            item.SpeedBps = 0;
            await PersistAsync(item, cancellationToken).ConfigureAwait(false);
            Notify(item);

            cookies = CookieVault.Unprotect(
                await _store.GetProtectedCookiesAsync(item.Id, cancellationToken).ConfigureAwait(false));
            var settings = _settings.Current;
            var progress = new Progress<DownloadProgress>(update => OnProgress(item, update));

            if (item.Kind == DownloadEngineKind.YtDlp)
            {
                await RunYtDlpAsync(item, settings, cookies, progress, cancellationToken).ConfigureAwait(false);
            }
            else if (item.Kind == DownloadEngineKind.Document)
            {
                await RunDocumentAsync(item, settings, cookies, progress, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RunHttpAsync(item, settings, cookies, progress, cancellationToken).ConfigureAwait(false);
            }

            if (running.CancelRequested)
            {
                item.State = DownloadState.Canceled;
            }
            else if (item.State != DownloadState.Completed && item.State != DownloadState.Failed)
            {
                item.State = DownloadState.Failed;
                item.Error ??= "Download failed.";
            }

            if (item.State == DownloadState.Completed)
            {
                item.CompletedAt = DateTimeOffset.UtcNow;
                await _store.ClearCookiesAsync(item.Id, CancellationToken.None).ConfigureAwait(false);
            }
            else if (item.State == DownloadState.Canceled)
            {
                await _store.ClearCookiesAsync(item.Id, CancellationToken.None).ConfigureAwait(false);
                CleanupPartial(item);
            }
        }
        catch (OperationCanceledException)
        {
            item.State = running.CancelRequested ? DownloadState.Canceled : DownloadState.Paused;
            item.SpeedBps = 0;
            if (running.CancelRequested)
            {
                await _store.ClearCookiesAsync(item.Id, CancellationToken.None).ConfigureAwait(false);
                CleanupPartial(item);
            }

            _logger.Info($"{item.State}: {item.Url}");
        }
        catch (Exception exception)
        {
            item.State = DownloadState.Failed;
            item.Error = exception.Message;
            item.SpeedBps = 0;
            _logger.Error($"Download crashed: {item.Url}", exception);
        }
        finally
        {
            item.SpeedBps = 0;
            lock (_gate)
            {
                _running.Remove(item.Id);
            }

            running.Cancellation.Dispose();

            var wasRemoved = false;
            lock (_gate)
            {
                wasRemoved = _removedIds.Contains(item.Id);
            }

            if (!wasRemoved)
            {
                await PersistAsync(item, CancellationToken.None).ConfigureAwait(false);
                Notify(item);
            }

            RaiseQueueChanged();
        }
    }

    private async Task RunHttpAsync(
        DownloadItem item,
        AppSettings settings,
        string? cookies,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        using var engine = new HttpDownloadEngine(
            settings.ProxyUrl,
            settings.UserAgent,
            _logger);

        var job = new DownloadJob
        {
            Url = item.Url,
            TargetDirectory = item.Directory,
            FileName = item.FileName,
            Category = item.Category,
            Connections = settings.Connections,
            SpeedLimitBps = settings.SpeedLimitBps,
            Referer = item.Referer,
            UserAgent = item.UserAgent ?? settings.UserAgent,
            Cookies = cookies
        };

        var outcome = await engine
            .DownloadAsync(job, progress, GetGlobalBucket(settings.SpeedLimitBps), cancellationToken)
            .ConfigureAwait(false);

        if (outcome.Success && outcome.FilePath is not null)
        {
            item.FileName = Path.GetFileName(outcome.FilePath);
            item.Directory = Path.GetDirectoryName(outcome.FilePath) ?? item.Directory;
            item.Category = CategoryResolver.FromFileName(item.FileName);
            item.FinalUrl = outcome.FinalUrl;
            item.TotalBytes = outcome.TotalBytes;
            item.DownloadedBytes = outcome.DownloadedBytes;
            item.ETag = outcome.ETag;
            item.LastModified = outcome.LastModified;
            item.State = DownloadState.Completed;
            item.Error = null;
            return;
        }

        item.State = DownloadState.Failed;
        item.Error = $"{outcome.ErrorCode ?? ErrorCodes.Internal}: {outcome.ErrorMessage}";
        if (outcome.DownloadedBytes > 0)
        {
            item.DownloadedBytes = outcome.DownloadedBytes;
        }

        if (outcome.TotalBytes > 0)
        {
            item.TotalBytes = outcome.TotalBytes;
        }
    }

    private async Task RunYtDlpAsync(
        DownloadItem item,
        AppSettings settings,
        string? cookies,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        var (preset, formatString) = ResolveYtDlpFormat(item.YtdlpFormat);
        var request = new YtDlpDownloadRequest
        {
            Url = item.Url,
            OutputDirectory = item.Directory,
            Preset = preset,
            FormatString = formatString,
            Cookies = cookies,
            CookiesFromBrowser = settings.CookiesFromBrowserEnabled ? settings.CookiesBrowser : null,
            Referer = item.Referer,
            UserAgent = item.UserAgent ?? settings.UserAgent,
            EmbedMetadata = settings.EmbedMetadata,
            EmbedThumbnail = settings.EmbedThumbnail,
            WriteSubtitles = settings.WriteSubtitles,
            SubtitleLanguages = settings.SubtitleLanguages,
            AllowPlaylist = false
        };

        var locator = ToolLocator.FromSettings(settings, _logger);
        var engine = new YtDlpEngine(locator, _logger);
        var outcome = await engine.DownloadAsync(request, progress, cancellationToken).ConfigureAwait(false);

        if (outcome.Success && outcome.FilePath is not null)
        {
            item.FileName = Path.GetFileName(outcome.FilePath);
            item.Directory = Path.GetDirectoryName(outcome.FilePath) ?? item.Directory;
            item.Category = CategoryResolver.FromFileName(item.FileName);
            item.TotalBytes = outcome.TotalBytes;
            item.DownloadedBytes = outcome.TotalBytes;
            item.State = DownloadState.Completed;
            item.Error = null;
            return;
        }

        item.State = DownloadState.Failed;
        item.Error = $"{outcome.ErrorCode ?? ErrorCodes.ExtractionFailed}: {outcome.ErrorMessage}";
    }

    private async Task RunDocumentAsync(
        DownloadItem item,
        AppSettings settings,
        string? cookies,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        var engine = new DocumentEngine(logger: _logger);
        var request = new DocumentDownloadRequest
        {
            Url = item.Url,
            OutputDirectory = item.Directory,
            FileName = item.FileName,
            Referer = item.Referer,
            UserAgent = item.UserAgent ?? settings.UserAgent,
            Cookies = cookies,
            Connections = settings.Connections
        };

        var outcome = await engine.DownloadAsync(request, progress, cancellationToken).ConfigureAwait(false);

        if (outcome.Success && outcome.FilePath is not null)
        {
            item.FileName = Path.GetFileName(outcome.FilePath);
            item.Directory = Path.GetDirectoryName(outcome.FilePath) ?? item.Directory;
            item.Category = CategoryResolver.FromFileName(item.FileName);
            item.TotalBytes = outcome.TotalBytes;
            item.DownloadedBytes = outcome.TotalBytes;
            item.State = DownloadState.Completed;
            item.Error = null;
            return;
        }

        item.State = DownloadState.Failed;
        item.Error = $"{outcome.ErrorCode ?? ErrorCodes.ExtractionFailed}: {outcome.ErrorMessage}";
    }

    private void OnProgress(DownloadItem item, DownloadProgress update)
    {
        if (update.DownloadedBytes > 0)
        {
            item.DownloadedBytes = update.DownloadedBytes;
        }

        if (update.TotalBytes > 0)
        {
            item.TotalBytes = update.TotalBytes;
        }

        item.SpeedBps = update.SpeedBps;

        if (update.Stage is "Merging" or "ExtractingAudio" or "PostProcessing")
        {
            item.State = DownloadState.Merging;
        }
        else if (item.State == DownloadState.Probing)
        {
            item.State = DownloadState.Downloading;
        }

        Notify(item);
    }

    private async Task PersistRunningProgressAsync(CancellationToken cancellationToken)
    {
        List<DownloadItem> snapshots;
        lock (_gate)
        {
            snapshots = _running.Keys
                .Select(id => _items.TryGetValue(id, out var item) ? item.Clone() : null)
                .Where(item => item is not null)
                .Select(item => item!)
                .ToList();
        }

        foreach (var item in snapshots)
        {
            try
            {
                await _store.UpdateProgressAsync(item, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.Warn($"Unable to persist progress for {item.Id}: {exception.Message}");
            }
        }
    }

    private void UpdateDrainedState()
    {
        bool drained;
        lock (_gate)
        {
            drained = _running.Count == 0 &&
                      !_items.Values.Any(item =>
                          item.State is DownloadState.Queued or DownloadState.WaitingForSchedule or
                              DownloadState.Probing or DownloadState.Downloading or DownloadState.Merging);
        }

        if (drained && !_drainedRaised)
        {
            _drainedRaised = true;
            if (_settings.Current.StopWhenQueueEmpty)
            {
                QueueDrained?.Invoke(this, EventArgs.Empty);
            }
        }
        else if (!drained)
        {
            _drainedRaised = false;
        }
    }

    private TokenBucket? GetGlobalBucket(long limit)
    {
        if (limit <= 0)
        {
            return null;
        }

        lock (_gate)
        {
            if (_globalBucket is null || _globalBucketLimit != limit)
            {
                _globalBucket = new TokenBucket(limit);
                _globalBucketLimit = limit;
            }

            return _globalBucket;
        }
    }

    private void CleanupPartial(DownloadItem item)
    {
        try
        {
            var basePath = Path.Combine(item.Directory, item.FileName);
            foreach (var suffix in new[] { HttpDownloadEngine.PartSuffix, HttpDownloadEngine.MetaSuffix })
            {
                var path = basePath + suffix;
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            if (item.Kind == DownloadEngineKind.YtDlp && Directory.Exists(item.Directory))
            {
                foreach (var partial in Directory.EnumerateFiles(item.Directory, "*.part"))
                {
                    var info = new FileInfo(partial);
                    if (info.LastWriteTimeUtc >= item.CreatedAt.UtcDateTime.AddMinutes(-1))
                    {
                        File.Delete(partial);
                    }
                }
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

    private async Task PersistAsync(DownloadItem item, CancellationToken cancellationToken)
    {
        try
        {
            await _store.UpsertAsync(item, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Warn($"Unable to persist {item.Id}: {exception.Message}");
        }
    }

    private void Notify(DownloadItem item)
    {
        DownloadItem clone;
        lock (_gate)
        {
            clone = item.Clone();
        }

        try
        {
            ItemUpdated?.Invoke(this, clone);
        }
        catch (Exception exception)
        {
            _logger.Warn($"ItemUpdated handler failed: {exception.Message}");
        }
    }

    private void NotifyDeleted(string id)
    {
        try
        {
            ItemDeleted?.Invoke(this, id);
        }
        catch (Exception exception)
        {
            _logger.Warn($"ItemDeleted handler failed: {exception.Message}");
        }
    }

    public event EventHandler<string>? ItemDeleted;

    private void RaiseQueueChanged()
    {
        try
        {
            QueueChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            _logger.Warn($"QueueChanged handler failed: {exception.Message}");
        }
    }

    private static string ResolveCategory(string? requestedCategory, string fileName, DownloadEngineKind kind)
    {
        if (!string.IsNullOrWhiteSpace(requestedCategory))
        {
            var match = MediaCategories.All.FirstOrDefault(category =>
                category.Equals(requestedCategory.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        if (kind == DownloadEngineKind.YtDlp)
        {
            return MediaCategories.Video;
        }

        if (kind == DownloadEngineKind.Document)
        {
            return MediaCategories.Documents;
        }

        return CategoryResolver.FromFileName(fileName);
    }

    private static string ResolveDirectory(AppSettings settings, string category)
    {
        var folder = settings.CategoryFolders.TryGetValue(category, out var configured) &&
                     !string.IsNullOrWhiteSpace(configured)
            ? configured
            : category;

        folder = SanitizeFolderName(folder);
        var root = Path.GetFullPath(settings.DownloadRoot);
        var directory = Path.Combine(root, folder);
        FilenamePolicy.EnsureWithinRoot(root, directory);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string SanitizeFolderName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalid.Contains(character) || character is '\\' or '/' ? '_' : character);
        }

        var sanitized = builder.ToString().Trim().Trim('.');
        return sanitized.Length == 0 || sanitized.Equals("..", StringComparison.Ordinal)
            ? MediaCategories.Other
            : sanitized;
    }

    private static (VideoPreset Preset, string? FormatString) ResolveYtDlpFormat(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (VideoPreset.Best, null);
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "best" => (VideoPreset.Best, null),
            "2160" or "2160p" => (VideoPreset.P2160, null),
            "1440" or "1440p" => (VideoPreset.P1440, null),
            "1080" or "1080p" => (VideoPreset.P1080, null),
            "720" or "720p" => (VideoPreset.P720, null),
            "480" or "480p" => (VideoPreset.P480, null),
            "mp3" => (VideoPreset.Mp3, null),
            "m4a" => (VideoPreset.M4a, null),
            _ => (VideoPreset.Best, value)
        };
    }

    public static bool IsWithinSchedule(AppSettings settings, DateTime now)
    {
        if (!settings.ScheduleEnabled)
        {
            return true;
        }

        var start = ParseClock(settings.ScheduleStart);
        var stop = ParseClock(settings.ScheduleStop);
        if (start is null && stop is null)
        {
            return true;
        }

        var current = now.TimeOfDay;
        if (start is not null && stop is not null)
        {
            return start <= stop
                ? current >= start && current < stop
                : current >= start || current < stop;
        }

        return start is not null ? current >= start : current < stop;
    }

    private static TimeSpan? ParseClock(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return TimeOnly.TryParse(value, CultureInfo.InvariantCulture, out var time)
            ? time.ToTimeSpan()
            : null;
    }

    public async Task ShutdownAsync()
    {
        _lifetime.Cancel();
        List<RunningDownload> running;
        lock (_gate)
        {
            running = _running.Values.ToList();
        }

        foreach (var run in running)
        {
            run.Cancellation.Cancel();
        }

        for (var index = 0; index < 50; index++)
        {
            lock (_gate)
            {
                if (_running.Count == 0)
                {
                    break;
                }
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        if (_scheduler is not null)
        {
            try
            {
                await _scheduler.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        List<DownloadItem> snapshots;
        lock (_gate)
        {
            snapshots = _items.Values
                .Where(item => item.State is not (DownloadState.Completed or DownloadState.Canceled))
                .Select(item => item.Clone())
                .ToList();
        }

        foreach (var item in snapshots)
        {
            try
            {
                await _store.UpsertAsync(item, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.Warn($"Unable to persist item on shutdown: {exception.Message}");
            }
        }

        _lifetime.Dispose();
    }

    public async ValueTask DisposeAsync() => await ShutdownAsync().ConfigureAwait(false);

    private sealed class RunningDownload(string id, string host, CancellationTokenSource cancellation)
    {
        public string Id { get; } = id;

        public string Host { get; } = host;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public bool PauseRequested { get; set; }

        public bool CancelRequested { get; set; }
    }
}
