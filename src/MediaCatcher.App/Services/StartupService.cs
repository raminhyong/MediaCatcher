using Microsoft.Win32;

namespace MediaCatcher.App.Services;

public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MediaCatcher";

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null)
            {
                return;
            }

            if (enabled)
            {
                var executable = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(executable))
                {
                    key.SetValue(ValueName, $"\"{executable}\" --minimized");
                }
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            // Autostart is best-effort.
        }
    }
}
