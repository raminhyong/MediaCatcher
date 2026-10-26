namespace MediaCatcher.Core.Utils;

public static class UrlPolicy
{
    private static readonly HashSet<string> RejectedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "file", "javascript", "data", "blob", "magnet", "ftp", "ws", "wss", "about", "chrome", "chrome-extension"
    };

    public static bool TryNormalize(string? rawUrl, out Uri uri, out string? error)
    {
        uri = null!;
        error = null;

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            error = "URL is empty.";
            return false;
        }

        if (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var parsed))
        {
            error = "URL is not absolute.";
            return false;
        }

        if (RejectedSchemes.Contains(parsed.Scheme))
        {
            error = $"URL scheme '{parsed.Scheme}' is not supported.";
            return false;
        }

        if (!parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = $"URL scheme '{parsed.Scheme}' is not supported.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.Host))
        {
            error = "URL has no host.";
            return false;
        }

        uri = parsed;
        return true;
    }

    public static bool IsSupported(string? rawUrl) => TryNormalize(rawUrl, out _, out _);

    public static string StripFragment(string url)
    {
        var hashIndex = url.IndexOf('#');
        return hashIndex >= 0 ? url[..hashIndex] : url;
    }

    public static string GetHost(string? url)
    {
        return TryNormalize(url, out var uri, out _) ? uri.Host.ToLowerInvariant() : string.Empty;
    }
}
