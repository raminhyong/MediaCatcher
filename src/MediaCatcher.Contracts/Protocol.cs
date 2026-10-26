namespace MediaCatcher.Contracts;

public static class Protocol
{
    public const int Version = 1;
    public const string HostName = "com.mediacatcher.host";
    public const string AppMutexNamePrefix = @"Local\MediaCatcher.App.";
    public const string PipeNamePrefix = "MediaCatcher-ipc-v1-";

    public const int MaxNativeMessageBytes = 64 * 1024 * 1024;
    public const int MaxResponseBytes = 1024 * 1024;

    public static readonly TimeSpan AddDownloadTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan AnalyzeTimeout = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan CombinePdfTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan AppStartTimeout = TimeSpan.FromSeconds(10);

    public static string PipeNameFor(string userSid) => PipeNamePrefix + userSid;

    public static string MutexNameFor(string userSid) => AppMutexNamePrefix + userSid;
}

public static class IpcActions
{
    public const string Ping = "ping";
    public const string AddDownload = "add-download";
    public const string AddBatch = "add-batch";
    public const string CombinePdf = "combine-pdf";
    public const string GetStatus = "get-status";
    public const string OpenApp = "open-app";
    public const string Analyze = "analyze";
    public const string Activate = "activate";
    public const string Shutdown = "shutdown";
}

public static class ErrorCodes
{
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string UnsupportedUrl = "UNSUPPORTED_URL";
    public const string Duplicate = "DUPLICATE";
    public const string AppNotStarted = "APP_NOT_STARTED";
    public const string AppNotInstalled = "APP_NOT_INSTALLED";
    public const string HostTimeout = "HOST_TIMEOUT";
    public const string ToolMissing = "TOOL_MISSING";
    public const string DiskFull = "DISK_FULL";
    public const string ExtractionFailed = "EXTRACTION_FAILED";
    public const string DrmUnsupported = "DRM_UNSUPPORTED";
    public const string QuotaExceeded = "QUOTA_EXCEEDED";
    public const string Internal = "INTERNAL";
}

public static class DownloadKindValues
{
    public const string Http = "http";
    public const string YtDlp = "ytdlp";
    public const string Document = "document";
}
