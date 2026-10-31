using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using MediaCatcher.App;

[assembly: AvaloniaTestApplication(typeof(MediaCatcher.App.Tests.TestAppBuilder))]

namespace MediaCatcher.App.Tests;

/// <summary>
/// Boots a headless Avalonia application so UI-level tests can run in CI without a desktop session.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Application>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
}

public sealed class HeadlessSmokeTests
{
    [AvaloniaFact]
    public void Window_opens_headless()
    {
        var window = new Window { Title = "Media Catcher", Width = 800, Height = 600 };

        window.Show();

        Assert.True(window.IsVisible);
        Assert.Equal("Media Catcher", window.Title);
    }

    [Fact]
    public void App_version_is_reported()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppInfo.Version));
    }
}
