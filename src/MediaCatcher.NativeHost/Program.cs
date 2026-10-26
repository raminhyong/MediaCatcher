using System.Diagnostics;
using System.Text;
using MediaCatcher.Contracts;

namespace MediaCatcher.NativeHost;

internal static class Program
{
    private static async Task<int> Main()
    {
        HostLog.Info("Native host started.");

        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        var client = new NamedPipeIpcClient();

        try
        {
            while (true)
            {
                byte[]? payload;
                try
                {
                    payload = await NativeMessagingFraming
                        .ReadMessageAsync(input, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (InvalidDataException exception)
                {
                    HostLog.Error("Native framing error; exiting.", exception);
                    return 1;
                }

                if (payload is null)
                {
                    HostLog.Info("Native host stdin closed.");
                    return 0;
                }

                if (payload.Length == 0)
                {
                    continue;
                }

                IpcResponse response;
                try
                {
                    var json = Encoding.UTF8.GetString(payload);
                    var nativeRequest = IpcJson.Deserialize<NativeRequest>(json);
                    response = nativeRequest is null
                        ? IpcResponse.Failure(string.Empty, ErrorCodes.InvalidRequest, "Empty request.")
                        : await HandleAsync(nativeRequest, client).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    HostLog.Error("Unhandled request error.", exception);
                    response = IpcResponse.Failure(string.Empty, ErrorCodes.Internal, exception.Message);
                }

                var responseJson = SerializeForChrome(response, out var oversized);
                if (oversized)
                {
                    responseJson = SerializeForChrome(
                        IpcResponse.Failure(response.Id, ErrorCodes.ExtractionFailed, "The response was too large."),
                        out _);
                }

                var bytes = Encoding.UTF8.GetBytes(responseJson);
                try
                {
                    await NativeMessagingFraming
                        .WriteMessageAsync(output, bytes, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (IOException exception)
                {
                    HostLog.Error("Native host stdout closed.", exception);
                    return 0;
                }
            }
        }
        catch (Exception exception)
        {
            HostLog.Error("Native host crashed.", exception);
            return 1;
        }
    }

    private static async Task<IpcResponse> HandleAsync(NativeRequest request, NamedPipeIpcClient client)
    {
        var action = (request.Action ?? string.Empty).Trim().ToLowerInvariant();
        var id = string.IsNullOrWhiteSpace(request.RequestId)
            ? Guid.NewGuid().ToString("N")
            : request.RequestId;
        var timeout = TimeoutFor(action);

        var ipcRequest = MapRequest(request, action, id);
        if (ipcRequest is null)
        {
            return IpcResponse.Failure(id, ErrorCodes.InvalidRequest, $"Unknown action '{request.Action}'.");
        }

        var response = await client.SendAsync(ipcRequest, timeout).ConfigureAwait(false);
        if (response.Code is not (ErrorCodes.AppNotStarted or ErrorCodes.HostTimeout))
        {
            return response;
        }

        var started = await TryStartAppAsync(client).ConfigureAwait(false);
        if (!started.Success)
        {
            return IpcResponse.Failure(
                id,
                started.Code,
                started.Message);
        }

        var retry = await client.SendAsync(ipcRequest, timeout).ConfigureAwait(false);
        return retry;
    }

    private static IpcRequest? MapRequest(NativeRequest request, string action, string id)
    {
        return action switch
        {
            IpcActions.Ping or IpcActions.GetStatus or IpcActions.OpenApp or IpcActions.Activate =>
                new IpcRequest
                {
                    Id = id,
                    Action = action,
                    Source = request.Source ?? "extension"
                },
            IpcActions.AddDownload => new IpcRequest
            {
                Id = id,
                Action = action,
                Source = request.Source ?? "extension",
                Download = new AddDownloadRequest
                {
                    Url = request.Url,
                    Kind = request.Kind,
                    FileName = request.FileName,
                    Referer = request.Referer,
                    Cookies = request.Cookies,
                    UserAgent = request.UserAgent,
                    Category = request.Category,
                    Engine = request.Engine,
                    Format = request.Format,
                    Takeover = request.Takeover
                }
            },
            IpcActions.AddBatch => new IpcRequest
            {
                Id = id,
                Action = action,
                Source = request.Source ?? "extension",
                Items = request.Items?.Take(500).ToList() ?? []
            },
            IpcActions.Analyze => new IpcRequest
            {
                Id = id,
                Action = action,
                Source = request.Source ?? "extension",
                Analyze = new AnalyzeRequest { Url = request.Url }
            },
            _ => null
        };
    }

    private static TimeSpan TimeoutFor(string action) => action switch
    {
        IpcActions.Analyze => Protocol.AnalyzeTimeout,
        IpcActions.AddBatch => TimeSpan.FromSeconds(30),
        _ => Protocol.AddDownloadTimeout
    };

    private static async Task<(bool Success, string Code, string Message)> TryStartAppAsync(
        NamedPipeIpcClient client)
    {
        var appPath = FindAppPath();
        if (appPath is null)
        {
            HostLog.Warn("MediaCatcher.App.exe was not found.");
            return (false, ErrorCodes.AppNotInstalled, "MediaCatcher.App.exe was not found. Reinstall Media Catcher.");
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = appPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(appPath) ?? AppContext.BaseDirectory
            };
            startInfo.ArgumentList.Add("--from-extension");
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (false, ErrorCodes.AppNotStarted, "The Media Catcher app could not be started.");
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            HostLog.Error("Unable to start the app.", exception);
            return (false, ErrorCodes.AppNotStarted, exception.Message);
        }

        var deadline = DateTimeOffset.UtcNow + Protocol.AppStartTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var ping = await client
                .SendAsync(IpcRequest.ForAction(IpcActions.Ping, "native-host"), TimeSpan.FromMilliseconds(800))
                .ConfigureAwait(false);
            if (ping.Ok)
            {
                HostLog.Info("App is ready.");
                return (true, string.Empty, string.Empty);
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        HostLog.Warn("App did not become ready in time.");
        return (false, ErrorCodes.AppNotStarted, "The Media Catcher app did not start in time.");
    }

    private static string? FindAppPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("MEDIACATCHER_APP_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        var baseDirectory = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDirectory, "MediaCatcher.App.exe"),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "MediaCatcher.App.exe")),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "MediaCatcher.App.exe"))
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string SerializeForChrome(IpcResponse response, out bool oversized)
    {
        var json = IpcJson.Serialize(response);
        if (Encoding.UTF8.GetByteCount(json) <= Protocol.MaxResponseBytes)
        {
            oversized = false;
            return json;
        }

        if (response.Analysis is not null)
        {
            var trimmed = response with
            {
                Analysis = response.Analysis with
                {
                    Formats = [],
                    Entries = []
                }
            };
            json = IpcJson.Serialize(trimmed);
            if (Encoding.UTF8.GetByteCount(json) <= Protocol.MaxResponseBytes)
            {
                oversized = false;
                return json;
            }
        }

        oversized = true;
        return json;
    }
}
