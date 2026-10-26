using System.Text;

namespace MediaCatcher.Core.Utils;

public static class ContentDispositionParser
{
    public static string? ExtractFileName(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return null;
        }

        var parts = SplitParameters(headerValue);
        string? extendedName = null;
        string? plainName = null;

        foreach (var part in parts)
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = part[..separator].Trim();
            var value = part[(separator + 1)..].Trim();

            if (key.Equals("filename*", StringComparison.OrdinalIgnoreCase))
            {
                extendedName = DecodeExtendedValue(value);
            }
            else if (key.Equals("filename", StringComparison.OrdinalIgnoreCase))
            {
                plainName = Unquote(value);
            }
        }

        var chosen = string.IsNullOrWhiteSpace(extendedName) ? plainName : extendedName;
        return string.IsNullOrWhiteSpace(chosen) ? null : chosen.Trim();
    }

    private static IEnumerable<string> SplitParameters(string headerValue)
    {
        var builder = new StringBuilder();
        var insideQuotes = false;

        foreach (var character in headerValue)
        {
            if (character == '"')
            {
                insideQuotes = !insideQuotes;
            }

            if (character == ';' && !insideQuotes)
            {
                yield return builder.ToString();
                builder.Clear();
                continue;
            }

            builder.Append(character);
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            trimmed = trimmed[1..^1];
            return trimmed
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        return trimmed;
    }

    private static string? DecodeExtendedValue(string value)
    {
        var unquoted = Unquote(value);
        var firstQuote = unquoted.IndexOf('\'');
        if (firstQuote < 0)
        {
            return null;
        }

        var secondQuote = unquoted.IndexOf('\'', firstQuote + 1);
        if (secondQuote < 0)
        {
            return null;
        }

        var encodingName = unquoted[..firstQuote];
        var encodedValue = unquoted[(secondQuote + 1)..];

        try
        {
            var encoding = string.IsNullOrWhiteSpace(encodingName)
                ? Encoding.UTF8
                : Encoding.GetEncoding(encodingName);
            return encoding.GetString(PercentDecode(encodedValue));
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static byte[] PercentDecode(string value)
    {
        using var output = new MemoryStream(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '%' && index + 2 < value.Length &&
                byte.TryParse(value.AsSpan(index + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var decoded))
            {
                output.WriteByte(decoded);
                index += 2;
                continue;
            }

            var bytes = Encoding.UTF8.GetBytes(value[index].ToString());
            output.Write(bytes);
        }

        return output.ToArray();
    }
}
