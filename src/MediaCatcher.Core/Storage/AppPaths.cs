namespace MediaCatcher.Core.Storage;

public sealed class AppPaths
{
    public AppPaths(string? appDataRoot = null, string? localAppDataRoot = null)
    {
        AppDataRoot = appDataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MediaCatcher");
        LocalAppDataRoot = localAppDataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MediaCatcher");
    }

    public string AppDataRoot { get; }

    public string LocalAppDataRoot { get; }

    public string SettingsFile => Path.Combine(AppDataRoot, "settings.json");

    public string DatabaseFile => Path.Combine(AppDataRoot, "MediaCatcher.db");

    public string LogDirectory => Path.Combine(LocalAppDataRoot, "logs");

    public string ToolsDirectory => Path.Combine(AppDirectory, "tools");

    public static string AppDirectory =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    public static string DefaultDownloadRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads",
        "MediaCatcher");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(AppDataRoot);
        Directory.CreateDirectory(LocalAppDataRoot);
        Directory.CreateDirectory(LogDirectory);
    }
}
