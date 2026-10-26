using System.Globalization;
using System.Text.RegularExpressions;
using MediaCatcher.Core.Models;

namespace MediaCatcher.Core.YtDlp;

public enum YtDlpLineKind
{
    Other,
    Progress,
    Destination,
    Stage,
    Error
}

public sealed record YtDlpLineEvent(
    YtDlpLineKind Kind,
    DownloadProgress? Progress = null,
    string? Destination = null,
    string? Stage = null,
    string? Error = null);

public static partial class YtDlpProgressParser
{
    public static YtDlpLineEvent ParseLine(string rawLine)
    {
        var line = (rawLine ?? string.Empty).TrimEnd('\r', '\n', ' ');
        if (line.Length == 0)
        {
            return new YtDlpLineEvent(YtDlpLineKind.Other);
        }

        if (line.StartsWith("PROGRESS ", StringComparison.Ordinal))
        {
            return new YtDlpLineEvent(
                YtDlpLineKind.Progress,
                Progress: ParseProgress(line["PROGRESS ".Length..]));
        }

        if (line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
        {
            return new YtDlpLineEvent(YtDlpLineKind.Error, Error: line);
        }

        var mergeMatch = MergeRegex().Match(line);
        if (mergeMatch.Success)
        {
            return new YtDlpLineEvent(
                YtDlpLineKind.Destination,
                Destination: mergeMatch.Groups["path"].Value,
                Stage: "Merging");
        }

        if (line.Contains("[ExtractAudio]", StringComparison.Ordinal))
        {
            var destination = ExtractAfter(line, "Destination:");
            return new YtDlpLineEvent(
                YtDlpLineKind.Destination,
                Destination: destination,
                Stage: "ExtractingAudio");
        }

        if (line.Contains("[Merger]", StringComparison.Ordinal))
        {
            return new YtDlpLineEvent(YtDlpLineKind.Stage, Stage: "Merging");
        }

        if (line.Contains("[Metadata]", StringComparison.Ordinal) ||
            line.Contains("[EmbedThumbnail]", StringComparison.Ordinal) ||
            line.Contains("[EmbedSubtitle]", StringComparison.Ordinal))
        {
            return new YtDlpLineEvent(YtDlpLineKind.Stage, Stage: "PostProcessing");
        }

        if (line.StartsWith("[download] Destination:", StringComparison.OrdinalIgnoreCase))
        {
            var destination = ExtractAfter(line, "Destination:");
            return new YtDlpLineEvent(YtDlpLineKind.Destination, Destination: destination);
        }

        if (line.StartsWith("[download] ", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("has already been downloaded", StringComparison.OrdinalIgnoreCase))
        {
            var destination = ExtractAfter(line, "has already been downloaded");
            return new YtDlpLineEvent(
                YtDlpLineKind.Destination,
                Destination: destination?.Trim().TrimEnd('.'),
                Stage: "Completed");
        }

        return new YtDlpLineEvent(YtDlpLineKind.Other);
    }

    public static DownloadProgress ParseProgress(string payload)
    {
        var fields = payload.Split('|');
        var downloaded = ParseLong(GetField(fields, 0));
        var total = ParseLong(GetField(fields, 1));
        var estimate = ParseLong(GetField(fields, 2));
        var speed = ParseDouble(GetField(fields, 3));
        var etaSeconds = ParseDouble(GetField(fields, 4));

        if (total <= 0)
        {
            total = estimate;
        }

        TimeSpan? eta = etaSeconds > 0 && etaSeconds < 7 * 24 * 3600
            ? TimeSpan.FromSeconds(etaSeconds)
            : null;

        return new DownloadProgress
        {
            DownloadedBytes = downloaded,
            TotalBytes = total,
            SpeedBps = speed,
            Eta = eta,
            Stage = "Downloading",
            ActiveSegments = 1
        };
    }

    public static bool LooksLikeDestination(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.Trim();
        return !trimmed.Contains('|') &&
               (trimmed.Contains('\\') || trimmed.Contains('/')) &&
               !trimmed.StartsWith("PROGRESS", StringComparison.Ordinal) &&
               !trimmed.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetField(string[] fields, int index) =>
        index < fields.Length ? fields[index].Trim() : null;

    private static long ParseLong(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals("NA", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Max(0, parsed)
            : 0;
    }

    private static double ParseDouble(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals("NA", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Max(0, parsed)
            : 0;
    }

    private static string? ExtractAfter(string line, string marker)
    {
        var index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var value = line[(index + marker.Length)..].Trim().Trim('"');
        return value.Length == 0 ? null : value;
    }

    [GeneratedRegex("Merging formats into \"(?<path>[^\"]+)\"")]
    private static partial Regex MergeRegex();
}
