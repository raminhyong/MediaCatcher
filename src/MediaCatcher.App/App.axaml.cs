using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MediaCatcher.App.Services;
using MediaCatcher.App.ViewModels;
using MediaCatcher.App.Views;
using MediaCatcher.Core.Logging;
using MediaCatcher.Core.Queue;
using MediaCatcher.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace MediaCatcher.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private AppHost? _host;
    private TrayIcon? _trayIcon;

    public static ServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = BuildServices();
            Services = _services;
            _host = _services.GetRequiredService<AppHost>();

            var firstRun = !File.Exists(_host.Paths.SettingsFile) || AppArguments.FirstRun;
            _host.InitializeAsync().GetAwaiter().GetResult();
            ThemeService.Apply(_host.Settings.Current.Theme);

            var viewModel = _services.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = viewModel };
            _host.MainWindow = window;
            desktop.MainWindow = window;

            _host.StartPipeServer();
            _host.ActivationRequested += (_, _) => Dispatcher.UIThread.Post(() =>
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
            });

            _trayIcon = TrayService.Create(window, viewModel);

            if (AppArguments.Minimized)
            {
                window.Opened += (_, _) => window.Hide();
            }

            if (firstRun)
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    var firstRunViewModel = _services!.GetRequiredService<FirstRunViewModel>();
                    await firstRunViewModel.InitializeAsync();
                    var firstRunWindow = new FirstRunWindow { DataContext = firstRunViewModel };
                    await firstRunWindow.ShowDialog(window);
                });
            }

            desktop.ShutdownRequested += (_, _) => window.AllowClose = true;
            desktop.Exit += (_, _) =>
            {
                _trayIcon?.Dispose();
                _trayIcon = null;
                viewModel.Dispose();
                _host?.ShutdownAsync().GetAwaiter().GetResult();
                _services?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton(_ => new AppPaths());
        services.AddSingleton<IAppLogger>(_ => new FileAppLogger());
        services.AddSingleton(provider => new SettingsService(provider.GetRequiredService<AppPaths>().SettingsFile));
        services.AddSingleton(provider => new DownloadStore(
            provider.GetRequiredService<AppPaths>().DatabaseFile,
            provider.GetRequiredService<IAppLogger>()));
        services.AddSingleton(provider => new QueueManager(
            provider.GetRequiredService<SettingsService>(),
            provider.GetRequiredService<DownloadStore>(),
            provider.GetRequiredService<IAppLogger>(),
            AppInfo.Version));
        services.AddSingleton<AppHost>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddTransient<AddUrlViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<FirstRunViewModel>();

        return services.BuildServiceProvider();
    }
}
