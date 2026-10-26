using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaCatcher.App.Localization;
using MediaCatcher.App.Services;
using MediaCatcher.Core.Logging;
using MediaCatcher.Core.Models;
using MediaCatcher.Core.Storage;
using MediaCatcher.Core.YtDlp;

namespace MediaCatcher.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly IAppLogger _logger;

    [ObservableProperty]
    private string _downloadRoot = string.Empty;

    [ObservableProperty]
    private int _connections = 8;

    [ObservableProperty]
    private int _speedLimitKbps;

    [ObservableProperty]
    private int _maxConcurrent = 3;

    [ObservableProperty]
    private int _perHostLimit = 2;

    [ObservableProperty]
    private string _proxyUrl = string.Empty;

    [ObservableProperty]
    private string _userAgent = string.Empty;

    [ObservableProperty]
    private bool _clipboardMonitorEnabled = true;

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private bool _minimizeToTray = true;

    [ObservableProperty]
    private bool _closeToTray = true;

    [ObservableProperty]
    private bool _cookiesFromBrowserEnabled;

    [ObservableProperty]
    private string _cookiesBrowser = "chrome";

    [ObservableProperty]
    private bool _ytDlpAutoUpdateEnabled = true;

    [ObservableProperty]
    private bool _embedMetadata = true;

    [ObservableProperty]
    private bool _embedThumbnail = true;

    [ObservableProperty]
    private bool _writeSubtitles;

    [ObservableProperty]
    private string _subtitleLanguages = "th,en";

    [ObservableProperty]
    private string _ytDlpPath = string.Empty;

    [ObservableProperty]
    private string _ffmpegPath = string.Empty;

    [ObservableProperty]
    private string _theme = "Dark";

    [ObservableProperty]
    private string _language = "th";

    [ObservableProperty]
    private bool _scheduleEnabled;

    [ObservableProperty]
    private string _scheduleStart = string.Empty;

    [ObservableProperty]
    private string _scheduleStop = string.Empty;

    [ObservableProperty]
    private bool _stopWhenQueueEmpty;

    [ObservableProperty]
    private bool _autoResumeOnLaunch = true;

    [ObservableProperty]
    private string _toolStatusText = string.Empty;

    [ObservableProperty]
    private string _hostStatusText = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private bool _hasMessage;

    [ObservableProperty]
    private bool _isBusy;

    public SettingsViewModel(SettingsService settings, IAppLogger logger)
    {
        _settings = settings;
        _logger = logger;

        Themes = ["Dark", "Light"];
        Languages = ["th", "en"];
        Browsers = ["chrome", "edge", "firefox"];

        Load(settings.Current);
    }

    public event Action<bool>? CloseRequested;

    public ObservableCollection<CategoryFolderSetting> CategoryFolders { get; } = [];

    public ObservableCollection<string> Themes { get; }

    public ObservableCollection<string> Languages { get; }

    public ObservableCollection<string> Browsers { get; }

    public async Task InitializeAsync()
    {
        await RefreshStatusAsync().ConfigureAwait(true);
    }

    private void Load(AppSettings settings)
    {
        DownloadRoot = settings.DownloadRoot;
        Connections = settings.Connections;
        SpeedLimitKbps = (int)Math.Clamp(settings.SpeedLimitBps / 1024, 0, int.MaxValue);
        MaxConcurrent = settings.MaxConcurrentDownloads;
        PerHostLimit = settings.PerHostLimit;
        ProxyUrl = settings.ProxyUrl ?? string.Empty;
        UserAgent = settings.UserAgent;
        ClipboardMonitorEnabled = settings.ClipboardMonitorEnabled;
        StartWithWindows = settings.StartWithWindows;
        MinimizeToTray = settings.MinimizeToTray;
        CloseToTray = settings.CloseToTray;
        CookiesFromBrowserEnabled = settings.CookiesFromBrowserEnabled;
        CookiesBrowser = settings.CookiesBrowser;
        YtDlpAutoUpdateEnabled = settings.YtDlpAutoUpdateEnabled;
        EmbedMetadata = settings.EmbedMetadata;
        EmbedThumbnail = settings.EmbedThumbnail;
        WriteSubtitles = settings.WriteSubtitles;
        SubtitleLanguages = settings.SubtitleLanguages;
        YtDlpPath = settings.YtDlpPath ?? string.Empty;
        FfmpegPath = settings.FfmpegPath ?? string.Empty;
        Theme = settings.Theme;
        Language = settings.Language;
        ScheduleEnabled = settings.ScheduleEnabled;
        ScheduleStart = settings.ScheduleStart ?? string.Empty;
        ScheduleStop = settings.ScheduleStop ?? string.Empty;
        StopWhenQueueEmpty = settings.StopWhenQueueEmpty;
        AutoResumeOnLaunch = settings.AutoResumeOnLaunch;

        CategoryFolders.Clear();
        foreach (var category in MediaCategories.All)
        {
            var folder = settings.CategoryFolders.TryGetValue(category, out var configured)
                ? configured
                : category;
            CategoryFolders.Add(new CategoryFolderSetting(category, folder));
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            var settings = _settings.Current.Clone();
            settings.DownloadRoot = DownloadRoot.Trim();
            settings.Connections = Connections;
            settings.SpeedLimitBps = Math.Max(0L, (long)SpeedLimitKbps * 1024);
            settings.MaxConcurrentDownloads = MaxConcurrent;
            settings.PerHostLimit = PerHostLimit;
            settings.ProxyUrl = string.IsNullOrWhiteSpace(ProxyUrl) ? null : ProxyUrl.Trim();
            settings.UserAgent = UserAgent.Trim();
            settings.ClipboardMonitorEnabled = ClipboardMonitorEnabled;
            settings.StartWithWindows = StartWithWindows;
            settings.MinimizeToTray = MinimizeToTray;
            settings.CloseToTray = CloseToTray;
            settings.CookiesFromBrowserEnabled = CookiesFromBrowserEnabled;
            settings.CookiesBrowser = CookiesBrowser;
            settings.YtDlpAutoUpdateEnabled = YtDlpAutoUpdateEnabled;
            settings.EmbedMetadata = EmbedMetadata;
            settings.EmbedThumbnail = EmbedThumbnail;
            settings.WriteSubtitles = WriteSubtitles;
            settings.SubtitleLanguages = SubtitleLanguages.Trim();
            settings.YtDlpPath = string.IsNullOrWhiteSpace(YtDlpPath) ? null : YtDlpPath.Trim();
            settings.FfmpegPath = string.IsNullOrWhiteSpace(FfmpegPath) ? null : FfmpegPath.Trim();
            settings.Theme = Theme;
            settings.Language = Language;
            settings.ScheduleEnabled = ScheduleEnabled;
            settings.ScheduleStart = string.IsNullOrWhiteSpace(ScheduleStart) ? null : ScheduleStart.Trim();
            settings.ScheduleStop = string.IsNullOrWhiteSpace(ScheduleStop) ? null : ScheduleStop.Trim();
            settings.StopWhenQueueEmpty = StopWhenQueueEmpty;
            settings.AutoResumeOnLaunch = AutoResumeOnLaunch;
            settings.CategoryFolders = CategoryFolders
                .Where(folder => !string.IsNullOrWhiteSpace(folder.Folder))
                .ToDictionary(folder => folder.Category, folder => folder.Folder.Trim(), StringComparer.OrdinalIgnoreCase);

            SettingsService.Normalize(settings);
            await _settings.SaveAsync(settings).ConfigureAwait(true);
            StartupService.SetEnabled(settings.StartWithWindows);
            ThemeService.Apply(settings.Theme);
            CloseRequested?.Invoke(true);
        }
        catch (Exception exception)
        {
            _logger.Warn($"Settings save failed: {exception.Message}");
            ShowMessage(exception.Message);
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);

    [RelayCommand]
    private async Task RefreshToolsAsync() => await RefreshStatusAsync().ConfigureAwait(true);

    [RelayCommand]
    private async Task FetchToolsAsync()
    {
        IsBusy = true;
        var result = await SetupActions.RunScriptAsync("fetch-tools.ps1").ConfigureAwait(true);
        IsBusy = false;
        ShowMessage(result.Message.Length == 0
            ? (result.Success ? Strings.Get("ReadyStatus") : Strings.Get("ToolMissing"))
            : result.Message);
        await RefreshStatusAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task UpdateYtDlpAsync()
    {
        IsBusy = true;
        try
        {
            var locator = new ToolLocator(
                string.IsNullOrWhiteSpace(YtDlpPath) ? null : YtDlpPath,
                string.IsNullOrWhiteSpace(FfmpegPath) ? null : FfmpegPath,
                logger: _logger);
            var engine = new YtDlpEngine(locator, _logger);
            var result = await engine.UpdateAsync().ConfigureAwait(true);
            ShowMessage(result.Message);
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshStatusAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RegisterHostAsync()
    {
        IsBusy = true;
        var result = await SetupActions.RunScriptAsync("install-native-host.ps1").ConfigureAwait(true);
        IsBusy = false;
        ShowMessage(result.Message.Length == 0
            ? (result.Success ? Strings.Get("ReadyStatus") : Strings.Get("HostNotInstalled"))
            : result.Message);
        await RefreshStatusAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task TestPingAsync()
    {
        IsBusy = true;
        var ping = await SetupActions.PingAsync().ConfigureAwait(true);
        IsBusy = false;
        ShowMessage(ping is null
            ? Strings.Get("AppNotRunning")
            : $"{Strings.Get("HostConnected")} · v{ping.AppVersion}");
        await RefreshStatusAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenExtensionFolder()
    {
        var folder = SetupActions.FindExtensionFolder();
        if (folder is null)
        {
            ShowMessage(Strings.Get("Missing"));
            return;
        }

        SetupActions.OpenFolder(folder);
    }

    private async Task RefreshStatusAsync()
    {
        var locator = new ToolLocator(
            string.IsNullOrWhiteSpace(YtDlpPath) ? null : YtDlpPath,
            string.IsNullOrWhiteSpace(FfmpegPath) ? null : FfmpegPath,
            logger: _logger);

        var tools = await locator.GetStatusAsync().ConfigureAwait(true);
        var ytDlpText = tools.YtDlpReady
            ? (tools.YtDlpVersion ?? Strings.Get("ReadyStatus"))
            : Strings.Get("Missing");
        var ffmpegText = tools.FfmpegReady ? Strings.Get("ReadyStatus") : Strings.Get("Missing");
        ToolStatusText = $"yt-dlp: {ytDlpText} · ffmpeg: {ffmpegText}";

        HostStatusText = SetupActions.IsHostRegistered()
            ? Strings.Get("HostConnected")
            : Strings.Get("HostNotInstalled");

        var ping = await SetupActions.PingAsync().ConfigureAwait(true);
        if (ping is not null)
        {
            HostStatusText += $" · v{ping.AppVersion}";
        }
    }

    private void ShowMessage(string message)
    {
        Message = message;
        HasMessage = !string.IsNullOrWhiteSpace(message);
    }
}

public sealed partial class CategoryFolderSetting : ObservableObject
{
    [ObservableProperty]
    private string _folder;

    public CategoryFolderSetting(string category, string folder)
    {
        Category = category;
        DisplayName = Strings.Get(category);
        _folder = folder;
    }

    public string Category { get; }

    public string DisplayName { get; }
}
