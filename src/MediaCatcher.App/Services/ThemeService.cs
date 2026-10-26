using Avalonia;
using Avalonia.Styling;
using Material.Styles.Themes;
using Material.Styles.Themes.Base;

namespace MediaCatcher.App.Services;

public static class ThemeService
{
    public static void Apply(string? theme)
    {
        var isLight = string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase);
        var variant = isLight ? ThemeVariant.Light : ThemeVariant.Dark;

        if (Application.Current is not { } application)
        {
            return;
        }

        application.RequestedThemeVariant = variant;
        var material = application.Styles.OfType<MaterialTheme>().FirstOrDefault();
        if (material is not null)
        {
            material.BaseTheme = isLight ? BaseThemeMode.Light : BaseThemeMode.Dark;
        }
    }
}
