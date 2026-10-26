using System.Text.RegularExpressions;

namespace MediaCatcher.NativeHost;

internal static partial class HostLog
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MediaCatcher",
                "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"nativehost-{DateTime.Now:yyyyMMdd}.log");
            var line = $"{DateTimeOffset.Now:O} [{level}] {Redact(message)}";
            if (exception is not null)
            {
                line += Environment.NewLine + Redact(exception.ToString());
            }

            lock (Gate)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch (IOException)
        {
            // Logging must never interfere with native messaging.
        }
        catch (UnauthorizedAccessException)
        {
            // Logging must never interfere with native messaging.
        }
    }

    private static string Redact(string value) =>
        SecretRegex().Replace(value, "$1=***");

    [GeneratedRegex(@"(?i)\b(cookie|authorization|token|password)\s*=\s*([^;\s,]+)")]
    private static partial Regex SecretRegex();
}
