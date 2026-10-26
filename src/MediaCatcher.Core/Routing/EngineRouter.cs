using MediaCatcher.Core.Models;
using MediaCatcher.Core.Utils;

namespace MediaCatcher.Core.Routing;

public static class EngineRouter
{
    private static readonly string[] YtDlpHostSuffixes =
    [
        "youtube.com", "youtu.be", "youtube-nocookie.com", "vimeo.com", "dailymotion.com",
        "soundcloud.com", "twitch.tv", "facebook.com", "instagram.com", "tiktok.com",
        "twitter.com", "x.com", "bilibili.com", "nicovideo.jp", "bandcamp.com",
        "archive.org", "rumble.com", "odysee.com", "brighteon.com", "streamable.com"
    ];

    private static readonly string[] StreamExtensions = [".m3u8", ".mpd"];

    private static readonly string[] DocumentHostSuffixes =
    [
        "anyflip.com", "fliphtml5.com", "online.fliphtml5.com", "pubhtml5.com", "heyzine.com",
        "publuu.com", "flowpaper.com", "flippingbook.com", "issuu.com", "paperturn.com"
    ];

    public static DownloadEngineKind Choose(string url, string? contentType = null, string? explicitEngine = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitEngine))
        {
            if (explicitEngine.Equals("ytdlp", StringComparison.OrdinalIgnoreCase) ||
                explicitEngine.Equals("yt-dlp", StringComparison.OrdinalIgnoreCase))
            {
                return DownloadEngineKind.YtDlp;
            }

            if (explicitEngine.Equals("http", StringComparison.OrdinalIgnoreCase))
            {
                return DownloadEngineKind.Http;
            }

            if (explicitEngine.Equals("document", StringComparison.OrdinalIgnoreCase) ||
                explicitEngine.Equals("flipbook", StringComparison.OrdinalIgnoreCase) ||
                explicitEngine.Equals("anyflip", StringComparison.OrdinalIgnoreCase))
            {
                return DownloadEngineKind.Document;
            }
        }

        if (IsDocumentHost(url))
        {
            return DownloadEngineKind.Document;
        }

        if (IsStreamUrl(url, contentType))
        {
            return DownloadEngineKind.YtDlp;
        }

        if (IsKnownExtractorHost(url))
        {
            return DownloadEngineKind.YtDlp;
        }

        return DownloadEngineKind.Http;
    }

    public static bool IsStreamUrl(string url, string? contentType = null)
    {
        var normalizedContentType = (contentType ?? string.Empty).ToLowerInvariant();
        if (normalizedContentType.Contains("mpegurl", StringComparison.Ordinal) ||
            normalizedContentType.Contains("dash+xml", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            var path = new Uri(url).AbsolutePath;
            return StreamExtensions.Any(extension =>
                path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    public static bool IsKnownExtractorHost(string url)
    {
        var host = UrlPolicy.GetHost(url);
        if (host.Length == 0)
        {
            return false;
        }

        return YtDlpHostSuffixes.Any(suffix =>
            host.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsDocumentHost(string url)
    {
        var host = UrlPolicy.GetHost(url);
        if (host.Length == 0)
        {
            return false;
        }

        return DocumentHostSuffixes.Any(suffix =>
            host.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase));
    }
}
