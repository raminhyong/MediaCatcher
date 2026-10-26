using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaCatcher.App.Localization;
using MediaCatcher.App.Services;
using MediaCatcher.Core.Logging;
using MediaCatcher.Core.Storage;
using MediaCatcher.Core.YtDlp;

namespace MediaCatcher.App.ViewModels;

public sealed partial class FirstRunViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly IAppLogger _logger;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private bool _hasMessage;

    [ObservableProperty]
    private bool _isBusy;

    public FirstRunViewModel(SettingsService settings, IAppLogger logger)
    {
        _settings = settings;
        _logger = logger;

        Checks =
        [
            new SetupCheck(Strings.Get("Tools")),
            new SetupCheck(Strings.Get("HostRegistration")),
            new SetupCheck(Strings.Get("Extension")),
            new SetupCheck(Strings.Get("TestPing"))
        ];
    }

    public event Action? CloseRequested;

    public ObservableCollection<SetupCheck> Checks { get; }

    public async Task InitializeAsync() => await RefreshAsync().ConfigureAwait(true);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var locator = ToolLocator.FromSettings(_settings.Current, _logger);
            var tools = await locator.GetStatusAsync().ConfigureAwait(true);
            Checks[0].Set(
                tools.YtDlpReady && tools.FfmpegReady,
                tools.YtDlpReady
                    ? $"yt-dlp: {tools.YtDlpVersion ?? Strings.Get("ReadyStatus")} · ffmpeg: " +
                      (tools.FfmpegReady ? Strings.Get("ReadyStatus") : Strings.Get("Missing"))
                    : Strings.Get("ToolMissing"));

            Checks[1].Set(
                SetupActions.IsHostRegistered(),
                SetupActions.IsHostRegistered() ? Strings.Get("HostConnected") : Strings.Get("HostNotInstalled"));

            var extensionFolder = SetupActions.FindExtensionFolder();
            Checks[2].Set(extensionFolder is not null, extensionFolder ?? Strings.Get("Missing"));

            var ping = await SetupActions.PingAsync().ConfigureAwait(true);
            Checks[3].Set(ping is not null, ping is null ? Strings.Get("AppNotRunning") : $"v{ping.AppVersion}");
        }
        finally
        {
            IsBusy = false;
        }
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
        await RefreshAsync().ConfigureAwait(true);
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

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    private void ShowMessage(string message)
    {
        Message = message;
        HasMessage = !string.IsNullOrWhiteSpace(message);
    }
}

public sealed partial class SetupCheck : ObservableObject
{
    [ObservableProperty]
    private bool _isOk;

    [ObservableProperty]
    private string _detail = string.Empty;

    public SetupCheck(string title)
    {
        Title = title;
    }

    public string Title { get; }

    public void Set(bool ok, string detail)
    {
        IsOk = ok;
        Detail = detail;
    }
}
