using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MediaCatcher.Core.Utils;

public static partial class FilenamePolicy
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private const int MaxFileNameLength = 140;

    public static string Sanitize(string? rawName, string fallbackExtension = "bin")
    {
        var name = (rawName ?? string.Empty).Trim();

        if (name.Length > 0)
        {
            try
            {
                name = Uri.UnescapeDataString(name);
            }
            catch (UriFormatException)
            {
                // Keep the original text when percent-decoding fails.
            }
        }

        name = name.Replace('\\', '_').Replace('/', '_');
        name = InvalidFileNameCharsRegex().Replace(name, "_");
        name = WhitespaceRegex().Replace(name, " ").Trim();
        name = name.TrimEnd('.', ' ');
        name = LeadingDotsRegex().Replace(name, string.Empty);

        if (name.Contains("..", StringComparison.Ordinal))
        {
            name = name.Replace("..", "_", StringComparison.Ordinal);
        }

        var lastDot = name.LastIndexOf('.');
        var hasExtension = lastDot > 0 && name.Length - lastDot <= 12;
        var extension = hasExtension ? name[lastDot..] : string.Empty;
        var baseName = hasExtension ? name[..lastDot] : name;

        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = $"media_{DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}";
        }

        if (ReservedNames.Contains(baseName))
        {
            baseName = "_" + baseName;
        }

        var maxBaseLength = MaxFileNameLength - extension.Length;
        if (baseName.Length > maxBaseLength)
        {
            baseName = baseName[..Math.Max(1, maxBaseLength)].TrimEnd('.', ' ');
        }

        if (string.IsNullOrWhiteSpace(extension))
        {
            var fallback = SanitizeExtension(fallbackExtension);
            extension = fallback.Length > 0 ? "." + fallback : string.Empty;
        }

        return baseName + extension;
    }

    public static string SanitizeExtension(string? extension)
    {
        var cleaned = (extension ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
        cleaned = Regex.Replace(cleaned, "[^a-z0-9]", string.Empty);
        return cleaned.Length > 10 ? cleaned[..10] : cleaned;
    }

    public static string DeriveFromUrl(string url, string fallbackExtension = "bin")
    {
        try
        {
            var uri = new Uri(url, UriKind.Absolute);
            var segment = uri.Segments.LastOrDefault()?.Trim('/');
            if (!string.IsNullOrWhiteSpace(segment))
            {
                return Sanitize(segment, fallbackExtension);
            }
        }
        catch (UriFormatException)
        {
            // Fall through to a generated name.
        }

        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return Sanitize($"media_{stamp}", fallbackExtension);
    }

    public static string EnsureUniquePath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate) && !File.Exists(candidate + ".mediacatcher.part"))
        {
            return candidate;
        }

        var extension = Path.GetExtension(fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        for (var index = 1; index < 10_000; index++)
        {
            var suffix = $" ({index.ToString(CultureInfo.InvariantCulture)})";
            var trimmedBase = baseName.Length + suffix.Length > MaxFileNameLength - extension.Length
                ? baseName[..Math.Max(1, MaxFileNameLength - extension.Length - suffix.Length)]
                : baseName;
            var candidateName = trimmedBase + suffix + extension;
            var candidatePath = Path.Combine(directory, candidateName);
            if (!File.Exists(candidatePath) && !File.Exists(candidatePath + ".mediacatcher.part"))
            {
                return candidatePath;
            }
        }

        return Path.Combine(directory, $"{baseName}_{Guid.NewGuid():N}{extension}");
    }

    public static string EnsureWithinRoot(string rootDirectory, string candidatePath)
    {
        var fullRoot = Path.GetFullPath(rootDirectory);
        var fullPath = Path.GetFullPath(candidatePath);
        var normalizedRoot = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!fullPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fullPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("The resolved path escapes the download root.");
        }

        return fullPath;
    }

    [GeneratedRegex(@"[<>:""/\\|?*\u0000-\u001F]")]
    private static partial Regex InvalidFileNameCharsRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^[.\s]+")]
    private static partial Regex LeadingDotsRegex();

    public static string ToDisplaySize(long bytes)
    {
        if (bytes < 0) return "-";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : $"{value.ToString("0.##", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    public static string NormalizeForComparison(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            builder.Append(char.IsLetterOrDigit(character) || character == '.' || character == '-'
                ? char.ToLowerInvariant(character)
                : '_');
        }

        return builder.ToString();
    }
}
