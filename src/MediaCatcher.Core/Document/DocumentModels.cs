namespace MediaCatcher.Core.Document;

public sealed record DocumentDownloadRequest
{
    public required string Url { get; init; }

    public required string OutputDirectory { get; init; }

    public string? FileName { get; init; }

    public string? Referer { get; init; }

    public string? UserAgent { get; init; }

    public string? Cookies { get; init; }

    public int Connections { get; init; } = 4;

    public int MaxPages { get; init; } = 500;
}

public sealed record DocumentPage
{
    public required string Url { get; init; }

    public int Index { get; init; }

    public string? Referer { get; init; }
}

public sealed record DocumentResolution(string? Title, IReadOnlyList<DocumentPage> Pages);

public sealed record DocumentOutcome
{
    public bool Success { get; init; }

    public string? FilePath { get; init; }

    public string? Title { get; init; }

    public int PageCount { get; init; }

    public long TotalBytes { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }
}
