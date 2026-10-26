using Avalonia;
using MediaCatcher.App.Services;
using MediaCatcher.Contracts;

namespace MediaCatcher.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppArguments.Capture(args);

        var sid = UserIdentity.ResolveCurrentUserSid();
        using var coordinator = new SingleInstanceCoordinator(Protocol.MutexNameFor(sid));
        if (!coordinator.IsFirstInstance)
        {
            SingleInstanceCoordinator
                .TryActivateExistingAsync(Protocol.PipeNameFor(sid))
                .GetAwaiter()
                .GetResult();
            return 0;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

internal static class AppArguments
{
    public static string[] Raw { get; private set; } = [];

    public static bool FromExtension => Has("--from-extension");

    public static bool FirstRun => Has("--first-run");

    public static bool Minimized => Has("--minimized");

    public static void Capture(string[] args) => Raw = args;

    private static bool Has(string value) =>
        Raw.Contains(value, StringComparer.OrdinalIgnoreCase);
}
