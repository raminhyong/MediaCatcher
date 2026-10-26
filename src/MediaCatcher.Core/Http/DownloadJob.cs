namespace MediaCatcher.Core.Http;

public sealed record DownloadJob
{
    public required string Url { get; init; }

    public required string TargetDirectory { get; init; }

    public string? FileName { get; init; }

    public string? Category { get; init; }

    public int Connections { get; init; } = 8;

    public long SpeedLimitBps { get; init; }

    public string? Referer { get; init; }

    public string? UserAgent { get; init; }

    public string? Cookies { get; init; }

    public bool ForceRestart { get; init; }
}
