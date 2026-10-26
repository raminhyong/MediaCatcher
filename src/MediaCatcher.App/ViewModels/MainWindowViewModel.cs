using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaCatcher.App.Localization;
using MediaCatcher.Contracts;
using MediaCatcher.Core.Logging;
using MediaCatcher.Core.Models;
using MediaCatcher.Core.Queue;
using MediaCatcher.Core.Storage;
using MediaCatcher.Core.Utils;

namespace MediaCatcher.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly QueueManager _queue;
    private readonly SettingsService _settings;
    private readonly IAppLogger _logger;
    private readonly Dictionary<string, DownloadItemViewModel> _byId = new(StringComparer.Ordinal);
    private readonly List<string> _categoryNames;
    private DispatcherTimer? _statusTimer;
    private DispatcherTimer? _clipboardTimer;
    private IClipboard? _clipboard;
    private string? _lastClipboardText;
    private bool _clipboardBusy;
    private bool _disposed;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private CategoryFilter? _selectedCategoryFilter;

    [ObservableProperty]
    private DownloadItemViewModel? _selectedItem;

    [ObservableProperty]
    private bool _hasSelection;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _speedText = string.Empty;

    [ObservableProperty]
    private string _clipboardSuggestion = string.Empty;

    [ObservableProperty]
    private bool _hasClipboardSuggestion;

    [ObservableProperty]
    private string _notification = string.Empty;

    [ObservableProperty]
    private bool _hasNotification;

    [ObservableProperty]
    private string _downloadRoot = string.Empty;

    [ObservableProperty]
    private int _activeCount;

    [ObservableProperty]
    private int _queuedCount;

    [ObservableProperty]
    private bool _isEmpty = true;

    public MainWindowViewModel(
        QueueManager queue,
        SettingsService settings,
        IAppLogger logger)
    {
        _queue = queue;
        _settings = settings;
        _logger = logger;

        _categoryNames = new List<string> { "All" };
        _categoryNames.AddRange(MediaCategories.All);

        foreach (var name in _categoryNames)
        {
            Categories.Add(new CategoryFilter(name));
        }

        SelectedCategoryFilter = Categories[0];
        DownloadRoot = settings.Current.DownloadRoot;

        _queue.ItemUpdated += OnItemUpdated;
        _queue.ItemDeleted += OnItemDeleted;
        _queue.QueueChanged += OnQueueChanged;

        ReloadItems();
        RefreshStatus();
    }

    public ObservableCollection<DownloadItemViewModel> Items { get; } = [];

    public ObservableCollection<DownloadItemViewModel> FilteredItems { get; } = [];

    public ObservableCollection<CategoryFilter> Categories { get; } = [];

    public string VersionText => $"v{AppInfo.Version}";

    public string DownloadsCountText => $"{Items.Count} {Strings.Get("DownloadsCount")}";

    public Func<Task<AddUrlResult?>>? ShowAddUrlDialog { get; set; }

    public Func<Task<bool>>? ShowSettingsDialog { get; set; }

    public Func<string, Task<bool>>? ConfirmAsync { get; set; }

    public Action? ExitRequested { get; set; }

    public void Start()
    {
        if (_statusTimer is null)
        {
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _statusTimer.Tick += (_, _) => RefreshStatus();
            _statusTimer.Start();
        }

        if (_clipboardTimer is null)
        {
            _clipboardTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clipboardTimer.Tick += OnClipboardTick;
            _clipboardTimer.Start();
        }
    }

    public void AttachClipboard(IClipboard clipboard)
    {
        _clipboard = clipboard;
    }

    private void ReloadItems()
    {
        Items.Clear();
        _byId.Clear();

        foreach (var item in _queue.GetItems().OrderByDescending(item => item.CreatedAt))
        {
            var viewModel = new DownloadItemViewModel(item);
            Items.Add(viewModel);
            _byId[item.Id] = viewModel;
        }

        RebuildFilteredItems();
        UpdateCategoryCounts();
        IsEmpty = Items.Count == 0;
    }

    private void OnItemUpdated(object? sender, DownloadItem item)
    {
        Dispatcher.UIThread.Post(() => ApplyItem(item));
    }

    private void OnItemDeleted(object? sender, string id)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_byId.Remove(id, out var viewModel))
            {
                Items.Remove(viewModel);
            }

            var filtered = FilteredItems.FirstOrDefault(entry => entry.Id == id);
            if (filtered is not null)
            {
                FilteredItems.Remove(filtered);
            }

            if (SelectedItem?.Id == id)
            {
                SelectedItem = null;
            }

            UpdateCategoryCounts();
            IsEmpty = Items.Count == 0;
        });
    }

    private void OnQueueChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(RefreshStatus);
    }

    private void ApplyItem(DownloadItem item)
    {
        if (!_byId.TryGetValue(item.Id, out var viewModel))
        {
            viewModel = new DownloadItemViewModel(item);
            _byId[item.Id] = viewModel;
            Items.Add(viewModel);
            if (MatchesFilter(viewModel))
            {
                InsertSorted(viewModel);
            }

            UpdateCategoryCounts();
            IsEmpty = false;
            return;
        }

        viewModel.Apply(item);
        UpdateCategoryCounts();
    }

    private void InsertSorted(DownloadItemViewModel viewModel)
    {
        var index = 0;
        while (index < FilteredItems.Count &&
               FilteredItems[index].Id != viewModel.Id &&
               FilteredItems[index].CreatedAt >= viewModel.CreatedAt)
        {
            index++;
        }

        if (FilteredItems.All(entry => entry.Id != viewModel.Id))
        {
            FilteredItems.Insert(index, viewModel);
        }
    }

    private bool MatchesFilter(DownloadItemViewModel item)
    {
        var category = SelectedCategoryFilter?.Name ?? "All";
        if (category != "All" &&
            !string.Equals(item.CategoryName, category, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var query = SearchText.Trim();
            if (!item.FileName.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !item.Url.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private void RebuildFilteredItems()
    {
        FilteredItems.Clear();
        foreach (var item in Items
                     .Where(MatchesFilter)
                     .OrderByDescending(item => item.CreatedAt))
        {
            FilteredItems.Add(item);
        }
    }

    private void UpdateCategoryCounts()
    {
        foreach (var category in Categories)
        {
            category.Count = category.Name == "All"
                ? Items.Count
                : Items.Count(item => string.Equals(item.CategoryName, category.Name, StringComparison.Ordinal));
        }

        OnPropertyChanged(nameof(DownloadsCountText));
    }

    partial void OnSearchTextChanged(string value) => RebuildFilteredItems();

    partial void OnSelectedCategoryFilterChanged(CategoryFilter? value) => RebuildFilteredItems();

    partial void OnSelectedItemChanged(DownloadItemViewModel? value) => HasSelection = value is not null;

    private void RefreshStatus()
    {
        var status = _queue.GetStatus();
        ActiveCount = status.ActiveCount;
        QueuedCount = status.QueuedCount;
        StatusText = status.ActiveCount > 0
            ? $"{Strings.Get("ActiveDownloads")}: {status.ActiveCount} · {Strings.Get("Queued")}: {status.QueuedCount}"
            : Strings.Get("Ready");
        SpeedText = status.SpeedBps > 1
            ? FilenamePolicy.ToDisplaySize(status.SpeedBps) + "/s"
            : string.Empty;
        DownloadRoot = _settings.Current.DownloadRoot;
    }

    [RelayCommand]
    private async Task AddUrlAsync()
    {
        if (ShowAddUrlDialog is null)
        {
            return;
        }

        var result = await ShowAddUrlDialog().ConfigureAwait(true);
        if (result is null || result.Items.Count == 0)
        {
            return;
        }

        await AddItemsAsync(result.Items).ConfigureAwait(true);
    }

    public async Task AddItemsAsync(IReadOnlyList<NewDownload> items)
    {
        var added = 0;
        var duplicates = 0;
        var failures = 0;

        foreach (var item in items)
        {
            try
            {
                var result = await _queue.AddAsync(item).ConfigureAwait(true);
                if (result.IsDuplicate)
                {
                    duplicates++;
                }
                else
                {
                    added++;
                }
            }
            catch (DownloadException exception)
            {
                failures++;
                ShowNotification(DescribeError(exception.Code, exception.Message));
            }
            catch (Exception exception)
            {
                failures++;
                _logger.Warn($"Add failed: {exception.Message}");
                ShowNotification(exception.Message);
            }
        }

        if (added > 0)
        {
            var message = $"{Strings.Get("Added")} ({added})";
            if (duplicates > 0)
            {
                message += $" · {Strings.Get("Duplicate")}: {duplicates}";
            }

            ShowNotification(message);
        }
        else if (duplicates > 0 && failures == 0)
        {
            ShowNotification(Strings.Get("Duplicate"));
        }
    }

    [RelayCommand]
    private async Task AddClipboardAsync()
    {
        if (string.IsNullOrWhiteSpace(ClipboardSuggestion))
        {
            return;
        }

        var url = ClipboardSuggestion;
        HasClipboardSuggestion = false;
        await AddItemsAsync([new NewDownload { Url = url, Source = "clipboard" }]).ConfigureAwait(true);
    }

    [RelayCommand]
    private void DismissClipboard() => HasClipboardSuggestion = false;

    [RelayCommand]
    private void DismissNotification() => HasNotification = false;

    [RelayCommand]
    private async Task PauseAllAsync() => await _queue.PauseAllAsync().ConfigureAwait(true);

    [RelayCommand]
    private async Task ResumeAllAsync() => await _queue.ResumeAllAsync().ConfigureAwait(true);

    [RelayCommand]
    private async Task PauseSelectedAsync()
    {
        if (SelectedItem is not null)
        {
            await _queue.PauseAsync(SelectedItem.Id).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ResumeSelectedAsync()
    {
        if (SelectedItem is not null)
        {
            await _queue.ResumeAsync(SelectedItem.Id).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task CancelSelectedAsync()
    {
        if (SelectedItem is not null)
        {
            await _queue.CancelAsync(SelectedItem.Id).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task RetrySelectedAsync()
    {
        if (SelectedItem is not null)
        {
            await _queue.RetryAsync(SelectedItem.Id).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task RemoveSelectedAsync()
    {
        if (SelectedItem is null)
        {
            return;
        }

        await _queue.RemoveAsync(SelectedItem.Id).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        var confirmed = ConfirmAsync is null || await ConfirmAsync(Strings.Get("ConfirmClearHistory")).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        await _queue.ClearHistoryAsync().ConfigureAwait(true);
        ReloadItems();
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        if (ShowSettingsDialog is null)
        {
            return;
        }

        var saved = await ShowSettingsDialog().ConfigureAwait(true);
        if (saved)
        {
            DownloadRoot = _settings.Current.DownloadRoot;
            ShowNotification(Strings.Get("Apply"));
        }
    }

    [RelayCommand]
    private void OpenFile()
    {
        var path = SelectedItem?.FilePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ShowNotification(Strings.Get("NoSelection"));
            return;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var item = SelectedItem;
        var directory = item?.Directory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            directory = _settings.Current.DownloadRoot;
        }

        if (!Directory.Exists(directory))
        {
            return;
        }

        var filePath = item?.FilePath;
        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{filePath}\"",
                UseShellExecute = true
            });
        }
        else
        {
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
    }

    [RelayCommand]
    private async Task CopyUrlAsync()
    {
        if (SelectedItem is null)
        {
            return;
        }

        if (_clipboard is not null)
        {
            await _clipboard.SetTextAsync(SelectedItem.Url).ConfigureAwait(true);
            ShowNotification(Strings.Get("CopyUrl"));
        }
    }

    private void OnClipboardTick(object? sender, EventArgs e)
    {
        if (_clipboard is null || _clipboardBusy || !_settings.Current.ClipboardMonitorEnabled)
        {
            return;
        }

        _ = CheckClipboardAsync();
    }

    private async Task CheckClipboardAsync()
    {
        if (_clipboard is null)
        {
            return;
        }

        _clipboardBusy = true;
        try
        {
            var text = (await _clipboard.TryGetTextAsync().ConfigureAwait(true))?.Trim();
            if (string.IsNullOrWhiteSpace(text) || string.Equals(text, _lastClipboardText, StringComparison.Ordinal))
            {
                return;
            }

            _lastClipboardText = text;
            if (!UrlPolicy.IsSupported(text) || Items.Any(item =>
                    string.Equals(item.Url, text, StringComparison.Ordinal)))
            {
                return;
            }

            ClipboardSuggestion = text;
            HasClipboardSuggestion = true;
        }
        catch (Exception exception)
        {
            _logger.Warn($"Clipboard read failed: {exception.Message}");
        }
        finally
        {
            _clipboardBusy = false;
        }
    }

    private void ShowNotification(string message)
    {
        Notification = message;
        HasNotification = !string.IsNullOrWhiteSpace(message);
    }

    private static string DescribeError(string? code, string? message)
    {
        return code switch
        {
            ErrorCodes.UnsupportedUrl => Strings.Get("UnsupportedUrl"),
            ErrorCodes.DrmUnsupported => Strings.Get("DrmUnsupported"),
            ErrorCodes.ToolMissing => Strings.Get("ToolMissing"),
            ErrorCodes.AppNotStarted or ErrorCodes.AppNotInstalled => Strings.Get("AppNotRunning"),
            _ => message ?? Strings.Get("Error")
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.ItemUpdated -= OnItemUpdated;
        _queue.ItemDeleted -= OnItemDeleted;
        _queue.QueueChanged -= OnQueueChanged;
        _statusTimer?.Stop();
        _clipboardTimer?.Stop();
    }
}

public sealed record AddUrlResult(IReadOnlyList<NewDownload> Items);
