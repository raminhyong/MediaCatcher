using System.Globalization;
using System.Resources;
using Avalonia.Markup.Xaml;

namespace MediaCatcher.App.Localization;

public static class Strings
{
    private static readonly ResourceManager Manager = new(
        "MediaCatcher.App.Resources.Strings",
        typeof(Strings).Assembly);

    public static string Get(string key)
    {
        try
        {
            return Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
        }
        catch (MissingManifestResourceException)
        {
            return key;
        }
    }

    public static void ApplyCulture(string? language)
    {
        var cultureName = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)
            ? "en-US"
            : "th-TH";
        var culture = CultureInfo.GetCultureInfo(cultureName);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
}

public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key)
    {
        Key = key;
    }

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Strings.Get(Key);
}
