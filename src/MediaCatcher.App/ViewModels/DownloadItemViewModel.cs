using CommunityToolkit.Mvvm.ComponentModel;
using MediaCatcher.App.Localization;
using MediaCatcher.Core.Models;
using MediaCatcher.Core.Utils;

namespace MediaCatcher.App.ViewModels;

public sealed class DownloadItemViewModel : ObservableObject
{
    private string _fileName = string.Empty;
    private string _url = string.Empty;
    private string _category = MediaCategories.Other;
    private string _categoryName = MediaCategories.Other;
    private DateTimeOffset _createdAt;
    private string _statusText = string.Empty;
    private string _sizeText = string.Empty;
    private string _progressText = string.Empty;
    private string _speedText = string.Empty;
    private string _etaText = string.Empty;
    private string _sourceText = string.Empty;
    private string _filePath = string.Empty;
    private string _directory = string.Empty;
    private string _error = string.Empty;
    private string _createdText = string.Empty;
    private string _hostText = string.Empty;
    private double _progressPercent;
    private DownloadState _state;
    private bool _canPause;
    private bool _canResume;
    private bool _canCancel;
    private bool _hasError;
    private bool _isCompleted;
    private long _downloadedBytes;
    private long _totalBytes;

    public DownloadItemViewModel(DownloadItem item)
    {
        Id = item.Id;
        Apply(item);
    }

    public string Id { get; }

    public string Url => _url;

    public string FileName => _fileName;

    public string Category => _category;

    public string CategoryName => _categoryName;

    public DateTimeOffset CreatedAt => _createdAt;

    public string StatusText => _statusText;

    public string SizeText => _sizeText;

    public string ProgressText => _progressText;

    public string SpeedText => _speedText;

    public string EtaText => _etaText;

    public string SourceText => _sourceText;

    public string FilePath => _filePath;

    public string Directory => _directory;

    public string Error => _error;

    public string CreatedText => _createdText;

    public string HostText => _hostText;

    public double ProgressPercent => _progressPercent;

    public DownloadState State => _state;

    public bool CanPause => _canPause;

    public bool CanResume => _canResume;

    public bool CanCancel => _canCancel;

    public bool HasError => _hasError;

    public bool IsCompleted => _isCompleted;

    public long DownloadedBytes => _downloadedBytes;

    public long TotalBytes => _totalBytes;

    public void Apply(DownloadItem item)
    {
        SetProperty(ref _url, item.Url, nameof(Url));
        SetProperty(ref _fileName, item.FileName, nameof(FileName));
        SetProperty(ref _category, Strings.Get(item.Category), nameof(Category));
        SetProperty(ref _categoryName, item.Category, nameof(CategoryName));
        _createdAt = item.CreatedAt;
        OnPropertyChanged(nameof(CreatedAt));
        SetProperty(ref _filePath, item.State == DownloadState.Completed ? Path.Combine(item.Directory, item.FileName) : string.Empty, nameof(FilePath));
        SetProperty(ref _directory, item.Directory, nameof(Directory));
        SetProperty(ref _hostText, item.Host, nameof(HostText));
        SetProperty(ref _error, item.Error ?? string.Empty, nameof(Error));
        SetProperty(ref _createdText, item.CreatedAt.LocalDateTime.ToString("g"), nameof(CreatedText));
        SetProperty(ref _sourceText, LocalizeSource(item.Source), nameof(SourceText));
        SetProperty(ref _state, item.State, nameof(State));

        _downloadedBytes = item.DownloadedBytes;
        _totalBytes = item.TotalBytes;
        OnPropertyChanged(nameof(DownloadedBytes));
        OnPropertyChanged(nameof(TotalBytes));

        var percent = item.TotalBytes > 0
            ? Math.Clamp(item.DownloadedBytes * 100.0 / item.TotalBytes, 0, 100)
            : item.State == DownloadState.Completed ? 100 : 0;
        SetProperty(ref _progressPercent, percent, nameof(ProgressPercent));

        var size = item.TotalBytes > 0 ? item.TotalBytes : item.DownloadedBytes;
        SetProperty(ref _sizeText, FilenamePolicy.ToDisplaySize(size), nameof(SizeText));

        var progressText = item.TotalBytes > 0
            ? $"{percent:0}% · {FilenamePolicy.ToDisplaySize(item.DownloadedBytes)} / {FilenamePolicy.ToDisplaySize(item.TotalBytes)}"
            : FilenamePolicy.ToDisplaySize(item.DownloadedBytes);
        SetProperty(ref _progressText, progressText, nameof(ProgressText));

        var isActive = item.State is DownloadState.Downloading or DownloadState.Probing or DownloadState.Merging;
        var speed = isActive ? item.SpeedBps : 0;
        SetProperty(
            ref _speedText,
            speed > 1 ? FilenamePolicy.ToDisplaySize((long)speed) + "/s" : "—",
            nameof(SpeedText));

        var remaining = Math.Max(0, item.TotalBytes - item.DownloadedBytes);
        SetProperty(ref _etaText, FormatEta(remaining, isActive ? speed : 0), nameof(EtaText));

        SetProperty(ref _statusText, Strings.Get(item.State.ToString()), nameof(StatusText));
        SetProperty(
            ref _canPause,
            item.State is DownloadState.Queued or DownloadState.WaitingForSchedule or
                DownloadState.Probing or DownloadState.Downloading or DownloadState.Merging,
            nameof(CanPause));
        SetProperty(
            ref _canResume,
            item.State is DownloadState.Paused or DownloadState.Failed or DownloadState.Canceled,
            nameof(CanResume));
        SetProperty(
            ref _canCancel,
            item.State is not (DownloadState.Completed or DownloadState.Canceled),
            nameof(CanCancel));
        SetProperty(ref _hasError, !string.IsNullOrWhiteSpace(item.Error), nameof(HasError));
        SetProperty(ref _isCompleted, item.State == DownloadState.Completed, nameof(IsCompleted));
    }

    private static string FormatEta(long remainingBytes, double speed)
    {
        if (remainingBytes <= 0 || speed <= 1)
        {
            return "—";
        }

        var seconds = remainingBytes / speed;
        if (seconds <= 0 || seconds > TimeSpan.MaxValue.TotalSeconds)
        {
            return "—";
        }

        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";
    }

    private static string LocalizeSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return Strings.Get("Unknown");
        }

        if (source.Contains("browser", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("extension", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("native", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("takeover", StringComparison.OrdinalIgnoreCase))
        {
            return Strings.Get("Browser");
        }

        if (source.Contains("clipboard", StringComparison.OrdinalIgnoreCase))
        {
            return Strings.Get("Clipboard");
        }

        return Strings.Get("Manual");
    }
}

public sealed class CategoryFilter : ObservableObject
{
    private int _count;

    public CategoryFilter(string name)
    {
        Name = name;
        DisplayName = Strings.Get(name);
    }

    public string Name { get; }

    public string DisplayName { get; }

    public int Count
    {
        get => _count;
        set => SetProperty(ref _count, value);
    }
}
