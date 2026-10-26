using MediaCatcher.Core.Models;

namespace MediaCatcher.Core.Utils;

public static class CategoryResolver
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".webm", ".mov", ".avi", ".flv", ".wmv", ".ts", ".mpg", ".mpeg", ".3gp", ".ogv"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".wav", ".flac", ".wma", ".aiff"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".avif", ".bmp", ".svg", ".tif", ".tiff", ".jxl", ".ico"
    };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".rtf", ".epub", ".mobi", ".csv", ".md", ".odt"
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso", ".cab"
    };

    private static readonly HashSet<string> ProgramExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".appx", ".apk", ".dmg", ".pkg", ".deb", ".rpm"
    };

    public static string FromFileName(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty);
        if (string.IsNullOrEmpty(extension))
        {
            return MediaCategories.Other;
        }

        if (VideoExtensions.Contains(extension)) return MediaCategories.Video;
        if (AudioExtensions.Contains(extension)) return MediaCategories.Audio;
        if (ImageExtensions.Contains(extension)) return MediaCategories.Images;
        if (DocumentExtensions.Contains(extension)) return MediaCategories.Documents;
        if (ArchiveExtensions.Contains(extension)) return MediaCategories.Archives;
        if (ProgramExtensions.Contains(extension)) return MediaCategories.Programs;
        return MediaCategories.Other;
    }

    public static string FromContentType(string? contentType)
    {
        var normalized = (contentType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
        if (normalized.StartsWith("video/", StringComparison.Ordinal)) return MediaCategories.Video;
        if (normalized.StartsWith("audio/", StringComparison.Ordinal)) return MediaCategories.Audio;
        if (normalized.StartsWith("image/", StringComparison.Ordinal)) return MediaCategories.Images;
        if (normalized.Contains("mpegurl", StringComparison.Ordinal) ||
            normalized.Contains("dash+xml", StringComparison.Ordinal))
        {
            return MediaCategories.Video;
        }

        return MediaCategories.Other;
    }

    public static string DefaultExtensionFor(string contentType)
    {
        var normalized = (contentType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
        return normalized switch
        {
            "video/mp4" => "mp4",
            "video/webm" => "webm",
            "video/x-matroska" => "mkv",
            "audio/mpeg" => "mp3",
            "audio/mp4" => "m4a",
            "audio/aac" => "aac",
            "audio/ogg" => "ogg",
            "image/png" => "png",
            "image/jpeg" => "jpg",
            "image/webp" => "webp",
            "image/gif" => "gif",
            "application/pdf" => "pdf",
            "application/zip" => "zip",
            "application/x-mpegurl" => "m3u8",
            "application/vnd.apple.mpegurl" => "m3u8",
            "application/dash+xml" => "mpd",
            _ => "bin"
        };
    }
}
