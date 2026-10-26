using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using MediaCatcher.App.Localization;
using MediaCatcher.App.Services;
using MediaCatcher.App.ViewModels;
using MediaCatcher.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace MediaCatcher.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += OnWindowClosing;
    }

    public bool AllowClose { get; set; }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        viewModel.ShowAddUrlDialog = ShowAddUrlDialogAsync;
        viewModel.ShowSettingsDialog = ShowSettingsDialogAsync;
        viewModel.ConfirmAsync = ConfirmAsync;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            viewModel.AttachClipboard(clipboard);
        }

        viewModel.Start();
    }

    private async Task<AddUrlResult?> ShowAddUrlDialogAsync()
    {
        var services = App.Services;
        if (services is null)
        {
            return null;
        }

        var viewModel = services.GetRequiredService<AddUrlViewModel>();
        var window = new AddUrlWindow { DataContext = viewModel };
        return await window.ShowDialog<AddUrlResult?>(this);
    }

    private async Task<bool> ShowSettingsDialogAsync()
    {
        var services = App.Services;
        if (services is null)
        {
            return false;
        }

        var viewModel = services.GetRequiredService<SettingsViewModel>();
        await viewModel.InitializeAsync();
        var window = new SettingsWindow { DataContext = viewModel };
        return await window.ShowDialog<bool>(this);
    }

    private async Task<bool> ConfirmAsync(string message)
    {
        var dialog = new Window
        {
            Title = Strings.Get("AppTitle"),
            Width = 440,
            Height = 200,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var messageBlock = new TextBlock
        {
            Text = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontSize = 14
        };

        var cancelButton = new Button { Content = "Cancel", MinWidth = 90 };
        var okButton = new Button { Content = "OK", MinWidth = 90 };

        cancelButton.Click += (_, _) => dialog.Close(false);
        okButton.Click += (_, _) => dialog.Close(true);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancelButton, okButton }
        };

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 20,
            Children = { messageBlock, buttons }
        };

        return await dialog.ShowDialog<bool>(this);
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (AllowClose)
        {
            return;
        }

        var settings = App.Services?.GetService<SettingsService>();
        if (settings?.Current.CloseToTray == true)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
