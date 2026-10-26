using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaCatcher.App.Localization;
using MediaCatcher.Contracts;
using MediaCatcher.Core.Logging;
using MediaCatcher.Core.Models;
using MediaCatcher.Core.Queue;
using MediaCatcher.Core.Routing;
using MediaCatcher.Core.Storage;
using MediaCatcher.Core.Utils;
using MediaCatcher.Core.YtDlp;

namespace MediaCatcher.App.ViewModels;

public sealed partial class AddUrlViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly IAppLogger _logger;

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _category = "Auto";

    [ObservableProperty]
    private string _selectedPreset = "best";

    [ObservableProperty]
    private MediaFormat? _selectedFormat;

    [ObservableProperty]
    private bool _isAnalyzing;

    [ObservableProperty]
    private bool _isAnalyzed;

    [ObservableProperty]
    private bool _isPlaylist;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _analysisSummary = string.Empty;

    [ObservableProperty]
    private string _error = string.Empty;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private bool _canAnalyze;

    [ObservableProperty]
    private int _playlistItemCount;

    public AddUrlViewModel(SettingsService settings, IAppLogger logger)
    {
        _settings = settings;
        _logger = logger;

        Categories = new ObservableCollection<string>(new[] { "Auto" }.Concat(MediaCategories.All));
        Presets = new ObservableCollection<string>(new[] { "best", "2160", "1440", "1080", "720", "480", "mp3", "m4a" });
        Formats = [];
    }

    public event Action<AddUrlResult?>? CloseRequested;

    public ObservableCollection<string> Categories { get; }

    public ObservableCollection<string> Presets { get; }

    public ObservableCollection<MediaFormat> Formats { get; }

    public bool IsYtDlpCandidate => UrlPolicy.IsSupported(Url) && EngineRouter.Choose(Url) == DownloadEngineKind.YtDlp;

    public bool ShowFormats => IsYtDlpCandidate;

    partial void OnUrlChanged(string value)
    {
        CanAnalyze = IsYtDlpCandidate;
        HasError = false;
        IsAnalyzed = false;
        IsPlaylist = false;
        Title = string.Empty;
        AnalysisSummary = string.Empty;
        _analysis = null;
        Formats.Clear();
        PlaylistItemCount = 0;
        OnPropertyChanged(nameof(ShowFormats));
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        HasError = false;
        Error = string.Empty;
        IsAnalyzing = true;

        try
        {
            if (!UrlPolicy.TryNormalize(Url, out var uri, out var urlError))
            {
                ShowError(Strings.Get("InvalidUrl") + " " + urlError);
                return;
            }

            if (EngineRouter.Choose(uri.AbsoluteUri) != DownloadEngineKind.YtDlp)
            {
                IsAnalyzed = true;
                Title = Strings.Get("DirectDownload");
                AnalysisSummary = uri.Host;
                return;
            }

            var locator = ToolLocator.FromSettings(_settings.Current, _logger);
            if (locator.Resolve().YtDlpPath is null)
            {
                ShowError(Strings.Get("ToolMissing"));
                return;
            }

            var engine = new YtDlpEngine(locator, _logger);
            var analysis = await engine.AnalyzeAsync(uri.AbsoluteUri).ConfigureAwait(true);
            _analysis = analysis;

            IsAnalyzed = true;
            Title = analysis.Title ?? uri.Host;
            AnalysisSummary = BuildSummary(analysis);

            if (analysis.IsPlaylist)
            {
                IsPlaylist = true;
                PlaylistItemCount = analysis.Entries.Count;
                return;
            }

            Formats.Clear();
            Formats.Add(new MediaFormat { FormatId = string.Empty, Label = Strings.Get("Preset") });
            foreach (var format in analysis.Formats)
            {
                Formats.Add(format);
            }

            SelectedFormat = Formats[0];
        }
        catch (DownloadException exception)
        {
            ShowError(exception.Code == ErrorCodes.ToolMissing
                ? Strings.Get("ToolMissing")
                : exception.Message);
        }
        catch (Exception exception)
        {
            _logger.Warn($"Analyze failed: {exception.Message}");
            ShowError(exception.Message);
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    [RelayCommand]
    private void Add()
    {
        if (!UrlPolicy.TryNormalize(Url, out var uri, out var urlError))
        {
            ShowError(Strings.Get("InvalidUrl") + " " + urlError);
            return;
        }

        var items = new List<NewDownload>();
        var category = Category == "Auto" ? null : Category;
        var fileName = string.IsNullOrWhiteSpace(FileName) ? null : FileName.Trim();
        var source = "manual";

        if (IsPlaylist && PlaylistItemCount > 0)
        {
            items.AddRange(PlaylistEntries(uri.AbsoluteUri, category, source));
        }
        else
        {
            var format = IsYtDlpCandidate
                ? string.IsNullOrWhiteSpace(SelectedFormat?.FormatId)
                    ? SelectedPreset
                    : SelectedFormat!.FormatId
                : null;

            items.Add(new NewDownload
            {
                Url = uri.AbsoluteUri,
                FileName = fileName,
                Category = category,
                Engine = IsYtDlpCandidate ? "ytdlp" : null,
                Format = format,
                Source = source
            });
        }

        if (items.Count == 0)
        {
            ShowError(Strings.Get("InvalidUrl"));
            return;
        }

        CloseRequested?.Invoke(new AddUrlResult(items));
    }

    private IEnumerable<NewDownload> PlaylistEntries(string fallbackUrl, string? category, string source)
    {
        var analysis = _analysis?.Entries;
        if (analysis is null || analysis.Count == 0)
        {
            yield return new NewDownload
            {
                Url = fallbackUrl,
                Category = category,
                Engine = "ytdlp",
                Format = SelectedPreset,
                Source = source
            };
            yield break;
        }

        foreach (var entry in analysis.Take(500))
        {
            if (string.IsNullOrWhiteSpace(entry.Url))
            {
                continue;
            }

            yield return new NewDownload
            {
                Url = entry.Url,
                Category = category ?? MediaCategories.Video,
                Engine = "ytdlp",
                Format = SelectedPreset,
                Source = source
            };
        }
    }

    private AnalyzeResult? _analysis;

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    private void ShowError(string message)
    {
        Error = message;
        HasError = true;
    }

    private static string BuildSummary(AnalyzeResult analysis)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(analysis.Uploader))
        {
            parts.Add(analysis.Uploader);
        }

        if (analysis.DurationSeconds is > 0)
        {
            var duration = TimeSpan.FromSeconds(analysis.DurationSeconds.Value);
            parts.Add(duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
                : $"{duration.Minutes}:{duration.Seconds:00}");
        }

        if (analysis.IsPlaylist)
        {
            parts.Add($"{Strings.Get("PlaylistItems")}: {analysis.ItemCount}");
        }

        return string.Join(" · ", parts);
    }
}
