using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using MediaCatcher.App.Localization;
using MediaCatcher.App.ViewModels;
using MediaCatcher.App.Views;

namespace MediaCatcher.App.Services;

public static class TrayService
{
    public static TrayIcon Create(MainWindow window, MainWindowViewModel viewModel)
    {
        var openItem = new NativeMenuItem(Strings.Get("ShowWindow"));
        openItem.Click += (_, _) => ShowWindow(window);

        var pauseItem = new NativeMenuItem(Strings.Get("PauseAll"));
        pauseItem.Click += (_, _) => viewModel.PauseAllCommand.Execute(null);

        var resumeItem = new NativeMenuItem(Strings.Get("ResumeAll"));
        resumeItem.Click += (_, _) => viewModel.ResumeAllCommand.Execute(null);

        var exitItem = new NativeMenuItem(Strings.Get("Exit"));
        exitItem.Click += (_, _) =>
        {
            window.AllowClose = true;
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
            {
                lifetime.Shutdown();
            }
        };

        var menu = new NativeMenu
        {
            Items =
            {
                openItem,
                new NativeMenuItemSeparator(),
                pauseItem,
                resumeItem,
                new NativeMenuItemSeparator(),
                exitItem
            }
        };

        var icon = new TrayIcon
        {
            ToolTipText = Strings.Get("AppTitle"),
            Menu = menu,
            IsVisible = true,
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://MediaCatcher.App/Assets/mediacatcher.ico")))
        };

        icon.Clicked += (_, _) => ShowWindow(window);
        return icon;
    }

    private static void ShowWindow(MainWindow window)
    {
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }
}
