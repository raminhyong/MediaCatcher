using System.Text.Json.Serialization;

namespace MediaCatcher.Contracts;

public sealed record NativeRequest
{
    public string? Action { get; init; }

    public string? RequestId { get; init; }

    public string? Source { get; init; }

    public string? Url { get; init; }

    public string? Kind { get; init; }

    public string? FileName { get; init; }

    public string? Referer { get; init; }

    public string? Cookies { get; init; }

    public string? UserAgent { get; init; }

    public string? Category { get; init; }

    public string? Engine { get; init; }

    public string? Format { get; init; }

    public bool? Takeover { get; init; }

    public List<AddDownloadRequest>? Items { get; init; }
}

public sealed record AddDownloadRequest
{
    public string? Url { get; init; }

    public string? Kind { get; init; }

    public string? FileName { get; init; }

    public string? Referer { get; init; }

    public string? Cookies { get; init; }

    public string? UserAgent { get; init; }

    public string? Category { get; init; }

    public string? Engine { get; init; }

    public string? Format { get; init; }

    public bool? Takeover { get; init; }
}

public sealed record AnalyzeRequest
{
    public string? Url { get; init; }
}

public sealed record IpcRequest
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Action { get; init; } = string.Empty;

    public string? Source { get; init; }

    public AddDownloadRequest? Download { get; init; }

    public List<AddDownloadRequest>? Items { get; init; }

    public AnalyzeRequest? Analyze { get; init; }

    public static IpcRequest ForAction(string action, string? source = null) =>
        new() { Action = action, Source = source };
}

public sealed record IpcResponse
{
    public string Id { get; init; } = string.Empty;

    public bool Ok { get; init; }

    public string? Code { get; init; }

    public string? Message { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DownloadId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Accepted { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Skipped { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PingInfo? Ping { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StatusInfo? Status { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnalyzeResult? Analysis { get; init; }

    public static IpcResponse Success(string id) => new() { Id = id, Ok = true };

    public static IpcResponse Failure(string id, string code, string? message = null) =>
        new() { Id = id, Ok = false, Code = code, Message = message };
}

public sealed record PingInfo
{
    public int Protocol { get; init; } = Contracts.Protocol.Version;

    public string AppVersion { get; init; } = "0.1.0";

    public string? HostVersion { get; init; }
}

public sealed record StatusInfo
{
    public bool Running { get; init; }

    public int ActiveCount { get; init; }

    public int QueuedCount { get; init; }

    public long SpeedBps { get; init; }

    public string AppVersion { get; init; } = "0.1.0";
}

public sealed record MediaFormat
{
    public string FormatId { get; init; } = string.Empty;

    public string? Label { get; init; }

    public string? Extension { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public double? Fps { get; init; }

    public string? VideoCodec { get; init; }

    public string? AudioCodec { get; init; }

    public long? FileSizeBytes { get; init; }

    public double? TotalBitrate { get; init; }

    public bool AudioOnly { get; init; }
}

public sealed record AnalyzeResult
{
    public string? Title { get; init; }

    public double? DurationSeconds { get; init; }

    public string? ThumbnailUrl { get; init; }

    public string? Uploader { get; init; }

    public bool IsPlaylist { get; init; }

    public int ItemCount { get; init; }

    public IReadOnlyList<MediaFormat> Formats { get; init; } = Array.Empty<MediaFormat>();

    public IReadOnlyList<AnalyzeEntry> Entries { get; init; } = Array.Empty<AnalyzeEntry>();
}

public sealed record AnalyzeEntry
{
    public string? Title { get; init; }

    public string? Url { get; init; }

    public double? DurationSeconds { get; init; }

    public string? ThumbnailUrl { get; init; }
}
