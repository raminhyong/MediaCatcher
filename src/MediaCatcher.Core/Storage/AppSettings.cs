using MediaCatcher.Core.Models;

namespace MediaCatcher.Core.Storage;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    public string DownloadRoot { get; set; } = AppPaths.DefaultDownloadRoot;

    public int Connections { get; set; } = 8;

    public long SpeedLimitBps { get; set; }

    public int MaxConcurrentDownloads { get; set; } = 3;

    public int PerHostLimit { get; set; } = 2;

    public string? ProxyUrl { get; set; }

    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/156.0.0.0 Safari/537.36";

    public Dictionary<string, string> CategoryFolders { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [MediaCategories.Video] = "Video",
        [MediaCategories.Audio] = "Audio",
        [MediaCategories.Images] = "Images",
        [MediaCategories.Documents] = "Documents",
        [MediaCategories.Archives] = "Archives",
        [MediaCategories.Programs] = "Programs",
        [MediaCategories.Other] = "Other"
    };

    public bool ClipboardMonitorEnabled { get; set; } = true;

    public bool StartWithWindows { get; set; }

    public bool MinimizeToTray { get; set; } = true;

    public bool CloseToTray { get; set; } = true;

    public bool CookiesFromBrowserEnabled { get; set; }

    public string CookiesBrowser { get; set; } = "chrome";

    public bool YtDlpAutoUpdateEnabled { get; set; } = true;

    public bool EmbedMetadata { get; set; } = true;

    public bool EmbedThumbnail { get; set; } = true;

    public bool WriteSubtitles { get; set; }

    public string SubtitleLanguages { get; set; } = "th,en";

    public string? YtDlpPath { get; set; }

    public string? FfmpegPath { get; set; }

    public string Theme { get; set; } = "Dark";

    public string Language { get; set; } = "th";

    public bool ScheduleEnabled { get; set; }

    public string? ScheduleStart { get; set; }

    public string? ScheduleStop { get; set; }

    public bool StopWhenQueueEmpty { get; set; }

    public bool AutoResumeOnLaunch { get; set; } = true;

    public AppSettings Clone()
    {
        var clone = (AppSettings)MemberwiseClone();
        clone.CategoryFolders = new Dictionary<string, string>(CategoryFolders, StringComparer.OrdinalIgnoreCase);
        return clone;
    }
}
