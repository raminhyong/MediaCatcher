using System.Diagnostics;
using System.Text;
using MediaCatcher.Contracts;
using Microsoft.Win32;

namespace MediaCatcher.App.Services;

public static class SetupActions
{
    public static string? FindScript(string scriptName)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "scripts", scriptName),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "scripts", scriptName)),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "MediaCatcher",
                "scripts",
                scriptName)
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    public static string? FindExtensionFolder()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "extension"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extension")),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "MediaCatcher",
                "extension")
        };

        return candidates.FirstOrDefault(path =>
            File.Exists(Path.Combine(path, "manifest.json")));
    }

    public static bool IsHostRegistered()
    {
        const string subKey = @"Software\Google\Chrome\NativeMessagingHosts\" + Protocol.HostName;
        const string edgeSubKey = @"Software\Microsoft\Edge\NativeMessagingHosts\" + Protocol.HostName;

        using var chrome = Registry.CurrentUser.OpenSubKey(subKey);
        using var edge = Registry.CurrentUser.OpenSubKey(edgeSubKey);
        return chrome is not null || edge is not null;
    }

    public static async Task<(bool Success, string Message)> RunScriptAsync(
        string scriptName,
        CancellationToken cancellationToken = default)
    {
        var scriptPath = FindScript(scriptName);
        if (scriptPath is null)
        {
            return (false, $"Script not found: {scriptName}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (false, "Unable to start PowerShell.");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));

            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already exited.
                }

                return (false, "The script timed out.");
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var combined = (stdout + "\n" + stderr).Trim();
            if (combined.Length > 4000)
            {
                combined = combined[^4000..];
            }

            return (process.ExitCode == 0, combined);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (false, exception.Message);
        }
    }

    public static async Task<PingInfo?> PingAsync(CancellationToken cancellationToken = default)
    {
        var client = new NamedPipeIpcClient();
        var response = await client
            .SendAsync(IpcRequest.ForAction(IpcActions.Ping, "settings"), TimeSpan.FromSeconds(3), cancellationToken)
            .ConfigureAwait(false);
        return response.Ok ? response.Ping : null;
    }

    public static void OpenFolder(string path)
    {
        if (Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }
}
