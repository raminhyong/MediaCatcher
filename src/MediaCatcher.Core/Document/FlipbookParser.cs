using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaCatcher.Core.Document;

public static partial class FlipbookParser
{
    private static readonly string[] PageContainerKeys = ["fliphtml5_pages", "pages", "pageList"];

    private static readonly string[] ImageValueKeys = ["n", "image", "images", "src", "url", "large", "file", "data"];

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif"
    };

    public static string? ExtractJsonObject(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return null;
        }

        var start = script.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = start; index < script.Length; index++)
        {
            var current = script[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (current == '"')
            {
                inString = true;
                continue;
            }

            if (current == '{')
            {
                depth++;
            }
            else if (current == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return script.Substring(start, index - start + 1);
                }
            }
        }

        return null;
    }

    public static string? ExtractTitle(string configJson)
    {
        if (!TryParse(configJson, out var document))
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            foreach (var key in new[] { "title", "bookTitle", "name", "book_name" })
            {
                var value = FindString(root, key);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return null;
        }
    }

    public static IReadOnlyList<string> ExtractPageUrls(string configJson, Uri baseUri)
    {
        if (!TryParse(configJson, out var document))
        {
            return [];
        }

        using (document)
        {
            JsonElement? pages = null;
            foreach (var key in PageContainerKeys)
            {
                pages = FindArray(document.RootElement, key);
                if (pages is not null)
                {
                    break;
                }
            }

            var collected = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (pages is not null)
            {
                foreach (var entry in pages.Value.EnumerateArray())
                {
                    CollectFromPageEntry(entry, collected, seen);
                }
            }
            else
            {
                CollectImageUrls(document.RootElement, collected, seen);
            }

            var result = new List<string>(collected.Count);
            var resultSeen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var candidate in collected)
            {
                if (TryResolveImageUrl(candidate, baseUri, out var resolved) && resultSeen.Add(resolved))
                {
                    result.Add(resolved);
                }
            }

            return result;
        }
    }

    public static string? FindConfigScriptUrl(string html, Uri pageUri)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var match = ConfigScriptRegex().Match(html);
        if (!match.Success)
        {
            return null;
        }

        var candidate = match.Groups[1].Value;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            uri = new Uri(pageUri, candidate);
        }

        return uri.AbsoluteUri;
    }

    private static void CollectFromPageEntry(JsonElement entry, List<string> output, HashSet<string> seen)
    {
        if (entry.ValueKind == JsonValueKind.String)
        {
            if (seen.Add(entry.GetString() ?? string.Empty))
            {
                output.Add(entry.GetString() ?? string.Empty);
            }

            return;
        }

        if (entry.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in entry.EnumerateArray())
            {
                CollectFromPageEntry(item, output, seen);
            }

            return;
        }

        if (entry.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var key in ImageValueKeys)
        {
            if (!entry.TryGetProperty(key, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var text = item.GetString() ?? string.Empty;
                        if (seen.Add(text))
                        {
                            output.Add(text);
                        }
                    }
                }
            }
            else if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString() ?? string.Empty;
                if (seen.Add(text))
                {
                    output.Add(text);
                }
            }
        }
    }

    private static void CollectImageUrls(JsonElement element, List<string> output, HashSet<string> seen)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var text = element.GetString() ?? string.Empty;
                if (seen.Add(text))
                {
                    output.Add(text);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectImageUrls(item, output, seen);
                }

                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    CollectImageUrls(property.Value, output, seen);
                }

                break;
        }
    }

    private static JsonElement? FindArray(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.Array)
                {
                    return property.Value;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                var found = FindArray(property.Value, name);
                if (found is not null)
                {
                    return found;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var found = FindArray(item, name);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static string? FindString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                var found = FindString(property.Value, name);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static bool TryResolveImageUrl(string candidate, Uri baseUri, out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var trimmed = candidate.Trim().Trim('"', '\'');
        Uri uri;
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute))
        {
            uri = absolute;
        }
        else
        {
            try
            {
                uri = new Uri(baseUri, trimmed);
            }
            catch (UriFormatException)
            {
                return false;
            }
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = uri.AbsolutePath;
        if (path.Contains("/thumb", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("thumbnail", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var extension = Path.GetExtension(uri.AbsolutePath);
        if (!ImageExtensions.Contains(extension))
        {
            return false;
        }

        resolved = uri.AbsoluteUri;
        return true;
    }

    private static bool TryParse(string json, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }

    [GeneratedRegex("""src\s*=\s*["']([^"']*(?:config|configuration)[^"']*\.js(?:\?[^"']*)?)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex ConfigScriptRegex();
}
