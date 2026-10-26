using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaCatcher.Core.Http;

public sealed class ResumeMetadata
{
    public int Version { get; set; } = 1;

    public string Url { get; set; } = string.Empty;

    public string? FinalUrl { get; set; }

    public long TotalBytes { get; set; }

    public string? ETag { get; set; }

    public string? LastModified { get; set; }

    public List<SegmentMetadata> Segments { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public long DownloadedBytes => Segments.Sum(segment => segment.Downloaded);

    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public sealed class SegmentMetadata
{
    private long _downloaded;

    public int Index { get; set; }

    public long Start { get; set; }

    public long End { get; set; }

    public long Downloaded
    {
        get => Interlocked.Read(ref _downloaded);
        set => Interlocked.Exchange(ref _downloaded, value);
    }

    public long Length => End >= Start ? End - Start + 1 : 0;

    public bool IsComplete => Length > 0 && Downloaded >= Length;

    public void AddDownloaded(long bytes) => Interlocked.Add(ref _downloaded, bytes);

    public long ReserveNextOffset()
    {
        var offset = Interlocked.Read(ref _downloaded);
        return Start + offset;
    }
}
