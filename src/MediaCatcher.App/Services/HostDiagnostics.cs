using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaCatcher.Contracts;
using Microsoft.Win32;

namespace MediaCatcher.App.Services;

public enum SetupIssueSeverity
{
    Info,
    Warning,
    Error
}

public sealed record SetupIssue(SetupIssueSeverity Severity, string Message);

/// <summary>
/// Validates the native messaging install: registry registration, host manifest schema,
/// pinned extension ID, and the external tools the engines need. A registered host whose
/// manifest is unparseable is reported to the extension as "host not found", so a
/// key-exists-only check hides exactly the failures that break the product.
/// </summary>
public static partial class HostDiagnostics
{
    private static readonly string[] HostManifestValuePaths =
    [
        $@"Software\Google\Chrome\NativeMessagingHosts\{Protocol.HostName}",
        $@"Software\Microsoft\Edge\NativeMessagingHosts\{Protocol.HostName}"
    ];

    public static IReadOnlyList<SetupIssue> Check()
    {
        var issues = new List<SetupIssue>();
        var manifestPath = FindRegisteredManifest(issues);

        string? extensionId = null;
        var extensionFolder = SetupActions.FindExtensionFolder();
        if (extensionFolder is null)
        {
            issues.Add(new SetupIssue(SetupIssueSeverity.Warning, "Extension folder was not found next to the app."));
        }
        else
        {
            extensionId = ReadPinnedExtensionId(Path.Combine(extensionFolder, "manifest.json"), issues);
        }

        if (manifestPath is not null)
        {
            ValidateHostManifest(manifestPath, extensionId, issues);
        }

        CheckTools(issues);
        return issues;
    }

    /// <summary>Computes the extension ID Chromium derives from a manifest "key" (SHA-256, hex mapped 0-f to a-p).</summary>
    public static string? ComputeExtensionId(string base64Key)
    {
        try
        {
            var der = Convert.FromBase64String(base64Key.Trim());
            var hash = SHA256.HashData(der);
            var hex = Convert.ToHexString(hash).ToLowerInvariant();
            var chars = new char[32];
            for (var i = 0; i < 32; i++)
            {
                chars[i] = (char)('a' + Convert.ToInt32(hex[i].ToString(), 16));
            }

            return new string(chars);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? FindRegisteredManifest(List<SetupIssue> issues)
    {
        string? manifestPath = null;
        foreach (var valuePath in HostManifestValuePaths)
        {
            using var key = Registry.CurrentUser.OpenSubKey(valuePath);
            var value = key?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            manifestPath ??= value;
        }

        if (manifestPath is null)
        {
            issues.Add(new SetupIssue(
                SetupIssueSeverity.Error,
                "Native messaging host is not registered. Run scripts/register-native-host.ps1, then reload the extension."));
        }

        return manifestPath;
    }

    private static string? ReadPinnedExtensionId(string extensionManifestPath, List<SetupIssue> issues)
    {
        if (!File.Exists(extensionManifestPath))
        {
            issues.Add(new SetupIssue(SetupIssueSeverity.Warning, $"Extension manifest missing: {extensionManifestPath}"));
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(extensionManifestPath));
            if (!document.RootElement.TryGetProperty("key", out var keyElement))
            {
                issues.Add(new SetupIssue(
                    SetupIssueSeverity.Warning,
                    "extension/manifest.json has no 'key', so its ID changes on reload and allowed_origins goes stale."));
                return null;
            }

            var id = ComputeExtensionId(keyElement.GetString() ?? string.Empty);
            if (id is null)
            {
                issues.Add(new SetupIssue(SetupIssueSeverity.Error, "extension/manifest.json 'key' is not valid base64."));
            }

            return id;
        }
        catch (JsonException exception)
        {
            issues.Add(new SetupIssue(SetupIssueSeverity.Error, $"extension/manifest.json is not valid JSON: {exception.Message}"));
            return null;
        }
    }

    private static void ValidateHostManifest(string manifestPath, string? expectedExtensionId, List<SetupIssue> issues)
    {
        if (!File.Exists(manifestPath))
        {
            issues.Add(new SetupIssue(SetupIssueSeverity.Error, $"Registered host manifest does not exist: {manifestPath}"));
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        }
        catch (JsonException exception)
        {
            issues.Add(new SetupIssue(SetupIssueSeverity.Error, $"Host manifest is not valid JSON: {exception.Message}"));
            return;
        }

        using (document)
        {
            var root = document.RootElement;

            if (!root.TryGetProperty("name", out var name) || name.GetString() != Protocol.HostName)
            {
                issues.Add(new SetupIssue(SetupIssueSeverity.Error, $"Host manifest name must be '{Protocol.HostName}'."));
            }

            if (!root.TryGetProperty("type", out var type) || type.GetString() != "stdio")
            {
                issues.Add(new SetupIssue(SetupIssueSeverity.Error, "Host manifest type must be 'stdio'."));
            }

            if (!root.TryGetProperty("path", out var pathElement) || !File.Exists(pathElement.GetString() ?? string.Empty))
            {
                issues.Add(new SetupIssue(
                    SetupIssueSeverity.Error,
                    "Host manifest 'path' does not point at an existing executable. Rebuild the solution or reinstall."));
            }

            if (!root.TryGetProperty("allowed_origins", out var origins) || origins.ValueKind != JsonValueKind.Array)
            {
                issues.Add(new SetupIssue(
                    SetupIssueSeverity.Error,
                    "Host manifest 'allowed_origins' must be a JSON array; Chromium rejects a scalar or missing value."));
                return;
            }

            var ids = new List<string>();
            foreach (var origin in origins.EnumerateArray())
            {
                var value = origin.GetString() ?? string.Empty;
                if (!OriginPattern().IsMatch(value))
                {
                    issues.Add(new SetupIssue(
                        SetupIssueSeverity.Error,
                        $"allowed_origins entry '{value}' is invalid. Only 'chrome-extension://<id>/' is accepted; an 'edge-extension://' entry makes Chromium reject the whole manifest."));
                    continue;
                }

                ids.Add(value["chrome-extension://".Length..].TrimEnd('/'));
            }

            if (ids.Count == 0)
            {
                issues.Add(new SetupIssue(SetupIssueSeverity.Error, "allowed_origins contains no usable extension origin."));
            }
            else if (expectedExtensionId is not null && !ids.Contains(expectedExtensionId))
            {
                issues.Add(new SetupIssue(
                    SetupIssueSeverity.Error,
                    $"allowed_origins does not list the pinned extension ID {expectedExtensionId}."));
            }
        }
    }

    private static void CheckTools(List<SetupIssue> issues)
    {
        var fetchScript = SetupActions.FindScript("fetch-tools.ps1");
        var scriptsDir = fetchScript is null ? null : Path.GetDirectoryName(fetchScript);
        var repoRoot = scriptsDir is null ? null : Path.GetFullPath(Path.Combine(scriptsDir, ".."));

        foreach (var tool in new[] { "yt-dlp.exe", "ffmpeg.exe" })
        {
            var found = repoRoot is not null && File.Exists(Path.Combine(repoRoot, "tools", tool));
            if (!found)
            {
                issues.Add(new SetupIssue(
                    SetupIssueSeverity.Warning,
                    $"tools/{tool} was not found; stream and video engines need it (run scripts/fetch-tools.ps1)."));
            }
        }
    }

    [GeneratedRegex(@"^chrome-extension://[a-p]{32}/$")]
    private static partial Regex OriginPattern();
}
