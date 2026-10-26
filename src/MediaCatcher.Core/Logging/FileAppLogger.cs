using System.Text.RegularExpressions;
using MediaCatcher.Core.Storage;
using MediaCatcher.Core.Utils;

namespace MediaCatcher.Core.Logging;

public sealed partial class FileAppLogger : IAppLogger
{
    private readonly object _gate = new();
    private readonly string _directory;

    public FileAppLogger(string? logDirectory = null)
    {
        _directory = logDirectory ?? new AppPaths().LogDirectory;
        Directory.CreateDirectory(_directory);
    }

    public string CurrentLogFile => Path.Combine(
        _directory,
        $"app-{DateTime.Now:yyyyMMdd}.log");

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message) => Write("WARN", message, null);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        var sanitized = RedactSecrets(message);
        var line = $"{DateTimeOffset.Now:O} [{level}] {sanitized}";
        if (exception is not null)
        {
            line += Environment.NewLine + RedactSecrets(exception.ToString());
        }

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                File.AppendAllText(CurrentLogFile, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // Logging must never crash the app.
            }
            catch (UnauthorizedAccessException)
            {
                // Logging must never crash the app.
            }
        }
    }

    public static string RedactSecrets(string value)
    {
        var redacted = SecretParameterRegex().Replace(value, "$1=***");
        redacted = CookieFlagRegex().Replace(redacted, "$1 ***");
        redacted = AuthorizationHeaderRegex().Replace(redacted, "$1: ***");
        return redacted;
    }

    [GeneratedRegex(@"(?i)\b(cookie|set-cookie|authorization|token|password|passwd|signature|sig|key)\s*=\s*([^;\s,]+)")]
    private static partial Regex SecretParameterRegex();

    [GeneratedRegex(@"(?i)(--cookies(?:-from-browser)?)\s+\S+")]
    private static partial Regex CookieFlagRegex();

    [GeneratedRegex(@"(?i)\b(authorization)\s*:\s*\S+")]
    private static partial Regex AuthorizationHeaderRegex();
}
