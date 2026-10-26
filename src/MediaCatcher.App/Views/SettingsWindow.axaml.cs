using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MediaCatcher.App.Localization;
using MediaCatcher.App.ViewModels;

namespace MediaCatcher.App.Views;

public partial class SettingsWindow : Window
{
    private SettingsViewModel? _viewModel;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested -= OnCloseRequested;
        }

        _viewModel = DataContext as SettingsViewModel;
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested += OnCloseRequested;
        }

        base.OnDataContextChanged(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested -= OnCloseRequested;
            _viewModel = null;
        }

        base.OnClosed(e);
    }

    private void OnCloseRequested(bool saved) => Close(saved);

    private async void OnBrowseFolder(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.Get("SelectFolder"),
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder is not null)
        {
            _viewModel.DownloadRoot = folder.Path.LocalPath;
        }
    }
}
