namespace MediaCatcher.Core.Models;

public enum DownloadState
{
    Queued,
    Probing,
    Downloading,
    Merging,
    Paused,
    Failed,
    Canceled,
    WaitingForSchedule,
    Completed
}

public enum DownloadEngineKind
{
    Http,
    YtDlp,
    Document
}

public static class MediaCategories
{
    public const string Video = "Video";
    public const string Audio = "Audio";
    public const string Images = "Images";
    public const string Documents = "Documents";
    public const string Archives = "Archives";
    public const string Programs = "Programs";
    public const string Other = "Other";

    public static readonly IReadOnlyList<string> All =
    [
        Video, Audio, Images, Documents, Archives, Programs, Other
    ];
}

public sealed class DownloadItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Url { get; set; } = string.Empty;

    public string? FinalUrl { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string Directory { get; set; } = string.Empty;

    public string Category { get; set; } = MediaCategories.Other;

    public DownloadEngineKind Kind { get; set; } = DownloadEngineKind.Http;

    public DownloadState State { get; set; } = DownloadState.Queued;

    public long TotalBytes { get; set; }

    public long DownloadedBytes { get; set; }

    public int Connections { get; set; } = 8;

    public string? ETag { get; set; }

    public string? LastModified { get; set; }

    public string? Referer { get; set; }

    public string? UserAgent { get; set; }

    public string? Engine { get; set; }

    public string? YtdlpFormat { get; set; }

    public string? Error { get; set; }

    public string? Source { get; set; }

    public int Priority { get; set; }

    public double SpeedBps { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public bool Takeover { get; set; }

    public DownloadItem Clone() => (DownloadItem)MemberwiseClone();

    public string Host
    {
        get
        {
            try
            {
                return new Uri(Url).Host;
            }
            catch (UriFormatException)
            {
                return string.Empty;
            }
        }
    }
}

public sealed record DownloadProgress
{
    public long DownloadedBytes { get; init; }

    public long TotalBytes { get; init; }

    public double SpeedBps { get; init; }

    public TimeSpan? Eta { get; init; }

    public string Stage { get; init; } = "Downloading";

    public int ActiveSegments { get; init; }

    public double Fraction => TotalBytes > 0
        ? Math.Clamp((double)DownloadedBytes / TotalBytes, 0, 1)
        : 0;
}

public sealed record HttpProbeResult
{
    public required string FinalUrl { get; init; }

    public required Uri FinalUri { get; init; }

    public long TotalBytes { get; init; }

    public bool SupportsRanges { get; init; }

    public string? ContentType { get; init; }

    public string? ETag { get; init; }

    public string? LastModified { get; init; }

    public string? ContentDisposition { get; init; }

    public int StatusCode { get; init; }
}

public sealed record DownloadOutcome
{
    public bool Success { get; init; }

    public string? FilePath { get; init; }

    public string? FinalUrl { get; init; }

    public long TotalBytes { get; init; }

    public long DownloadedBytes { get; init; }

    public string? ETag { get; init; }

    public string? LastModified { get; init; }

    public string? ContentType { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }
}

public sealed class DownloadException : Exception
{
    public DownloadException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
