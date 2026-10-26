using System.Globalization;

namespace MediaCatcher.Core.YtDlp;

public enum VideoPreset
{
    Best,
    P2160,
    P1440,
    P1080,
    P720,
    P480,
    Mp3,
    M4a
}

public sealed record YtDlpDownloadRequest
{
    public required string Url { get; init; }

    public required string OutputDirectory { get; init; }

    public string? FormatString { get; init; }

    public VideoPreset Preset { get; init; } = VideoPreset.Best;

    public string? CookiesFile { get; init; }

    public string? Cookies { get; init; }

    public string? CookiesFromBrowser { get; init; }

    public string? Referer { get; init; }

    public string? UserAgent { get; init; }

    public string? FfmpegDirectory { get; init; }

    public bool EmbedMetadata { get; init; } = true;

    public bool EmbedThumbnail { get; init; } = true;

    public bool WriteSubtitles { get; init; }

    public string SubtitleLanguages { get; init; } = "th,en";

    public bool AllowPlaylist { get; init; }

    public int PlaylistLimit { get; init; } = 500;

    public string OutputTemplate { get; init; } = "%(title).150B [%(id)s].%(ext)s";

}

public static class YtDlpArgumentBuilder
{
    public const string ProgressTemplate =
        "download:PROGRESS %(progress.downloaded_bytes)s|%(progress.total_bytes)s|" +
        "%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s";

    public static IReadOnlyList<string> BuildDownloadArguments(YtDlpDownloadRequest request)
    {
        var arguments = new List<string>
        {
            "--ignore-config",
            "--newline",
            "--no-colors",
            "--no-simulate",
            "--progress",
            "--windows-filenames",
            "--trim-filenames",
            "140",
            "--socket-timeout",
            "20",
            "--retries",
            "5",
            "--fragment-retries",
            "5",
            "--progress-template",
            ProgressTemplate,
            "--print",
            "after_move:filepath",
            "-P",
            request.OutputDirectory,
            "-o",
            request.OutputTemplate,
            "-f"
        };

        var (format, postProcessAudio) = ResolveFormat(request);
        arguments.Add(format);

        var isAudioPreset = request.Preset is VideoPreset.Mp3 or VideoPreset.M4a;
        if (!isAudioPreset && string.IsNullOrWhiteSpace(request.FormatString))
        {
            arguments.Add("--merge-output-format");
            arguments.Add("mp4");
        }

        if (postProcessAudio is not null)
        {
            arguments.Add("-x");
            arguments.Add("--audio-format");
            arguments.Add(postProcessAudio);
        }

        if (string.Equals(postProcessAudio, "mp3", StringComparison.Ordinal))
        {
            arguments.Add("--audio-quality");
            arguments.Add("0");
        }

        if (request.AllowPlaylist)
        {
            arguments.Add("--playlist-end");
            arguments.Add(Math.Clamp(request.PlaylistLimit, 1, 500).ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            arguments.Add("--no-playlist");
        }

        if (request.EmbedMetadata)
        {
            arguments.Add("--embed-metadata");
        }

        if (request.EmbedThumbnail)
        {
            arguments.Add("--embed-thumbnail");
        }

        if (request.WriteSubtitles)
        {
            arguments.Add("--embed-subs");
            arguments.Add("--write-subs");
            arguments.Add("--sub-langs");
            arguments.Add(request.SubtitleLanguages);
        }

        if (!string.IsNullOrWhiteSpace(request.CookiesFile))
        {
            arguments.Add("--cookies");
            arguments.Add(request.CookiesFile);
        }
        else if (!string.IsNullOrWhiteSpace(request.CookiesFromBrowser))
        {
            arguments.Add("--cookies-from-browser");
            arguments.Add(request.CookiesFromBrowser);
        }

        if (!string.IsNullOrWhiteSpace(request.Referer))
        {
            arguments.Add("--referer");
            arguments.Add(request.Referer);
        }

        if (!string.IsNullOrWhiteSpace(request.UserAgent))
        {
            arguments.Add("--user-agent");
            arguments.Add(request.UserAgent);
        }

        if (!string.IsNullOrWhiteSpace(request.FfmpegDirectory))
        {
            arguments.Add("--ffmpeg-location");
            arguments.Add(request.FfmpegDirectory);
        }

        return arguments;
    }

    public static IReadOnlyList<string> BuildAnalyzeArguments(string url, bool flatPlaylist, int playlistLimit = 500)
    {
        var arguments = new List<string>
        {
            "-J",
            "--ignore-config",
            "--no-warnings",
            "--no-colors",
            "--socket-timeout",
            "20",
            "--retries",
            "3",
            "--playlist-end",
            Math.Clamp(playlistLimit, 1, 500).ToString(CultureInfo.InvariantCulture)
        };

        if (flatPlaylist)
        {
            arguments.Add("--flat-playlist");
        }

        arguments.Add(url);
        return arguments;
    }

    public static (string Format, string? AudioFormat) ResolveFormat(YtDlpDownloadRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.FormatString))
        {
            return (request.FormatString, null);
        }

        return request.Preset switch
        {
            VideoPreset.Mp3 => ("ba/b", "mp3"),
            VideoPreset.M4a => ("ba[ext=m4a]/ba/b", "m4a"),
            VideoPreset.P2160 => ("bv*[height<=2160]+ba/b[height<=2160]/b", null),
            VideoPreset.P1440 => ("bv*[height<=1440]+ba/b[height<=1440]/b", null),
            VideoPreset.P1080 => ("bv*[height<=1080]+ba/b[height<=1080]/b", null),
            VideoPreset.P720 => ("bv*[height<=720]+ba/b[height<=720]/b", null),
            VideoPreset.P480 => ("bv*[height<=480]+ba/b[height<=480]/b", null),
            _ => ("bv*+ba/b", null)
        };
    }
}
