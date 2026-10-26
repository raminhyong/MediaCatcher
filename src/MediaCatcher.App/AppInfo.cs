using System.Reflection;

namespace MediaCatcher.App;

public static class AppInfo
{
    public static string Version
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? "0.1.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }
}
