using System.Diagnostics;
using Adm.Core.Ipc;

namespace Adm.NativeHost;

/// <summary>
/// Chrome/Edge/Firefox native messaging host. Reads one framed JSON request on
/// stdin, forwards it to the running ADM instance over a named pipe, and writes
/// one framed JSON reply on stdout. If ADM is not running it is launched first.
/// </summary>
internal static class Program
{
    private static async Task<int> Main()
    {
        try
        {
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();

            var request = await NativeMessagingProtocol.ReadAsync(input).ConfigureAwait(false);
            if (request is null)
                return 0;

            var reply = await ForwardAsync(request).ConfigureAwait(false);
            await NativeMessagingProtocol.WriteAsync(output, reply).ConfigureAwait(false);
            return reply.Ok ? 0 : 1;
        }
        catch (EndOfStreamException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                using var output = Console.OpenStandardOutput();
                await NativeMessagingProtocol.WriteAsync(output,
                    new NativeReply { Ok = false, Message = ex.Message }).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // stdout may already be closed.
            }

            return 1;
        }
    }

    private static async Task<NativeReply> ForwardAsync(NativeRequest request)
    {
        string action = (request.Action ?? "ADD_DOWNLOAD").Trim().ToUpperInvariant();

        if (action is "PING")
        {
            var ping = await PipeClient.SendAsync(new PipeMessage { Action = "PING" }, TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);

            return ping is null
                ? new NativeReply { Ok = false, Message = "ADM is not running." }
                : new NativeReply { Ok = true, Message = "Running" };
        }

        if (action is "GET_INTERCEPT" or "SET_INTERCEPT")
        {
            var interceptMessage = new PipeMessage
            {
                Action = action,
                Intercept = request.Intercept
            };

            // This is a status query, so the app is never started here. The
            // extension keeps its own copy of the setting and syncs it when
            // ADM is already running.
            var interceptReply = await PipeClient.SendAsync(interceptMessage, TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);

            return interceptReply is null
                ? new NativeReply { Ok = false, Message = "ADM is not running." }
                : new NativeReply { Ok = interceptReply.Ok, Message = interceptReply.Message };
        }

        if (action is "SHOW")
        {
            var shown = await PipeClient.SendAsync(new PipeMessage { Action = "SHOW" }, TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);

            if (shown is not null)
                return new NativeReply { Ok = shown.Ok, Message = shown.Message };

            LaunchApp(null);
            shown = await PipeClient.SendAsync(new PipeMessage { Action = "SHOW" }, TimeSpan.FromSeconds(20))
                .ConfigureAwait(false);

            return shown is null
                ? new NativeReply { Ok = false, Message = "ADM could not be started." }
                : new NativeReply { Ok = shown.Ok, Message = shown.Message };
        }

        if (string.IsNullOrWhiteSpace(request.Url) && string.IsNullOrWhiteSpace(request.Urls))
            return new NativeReply { Ok = false, Message = "No URL was supplied." };

        if (action is "ADD_DOWNLOADS")
        {
            var bulkMessage = new PipeMessage
            {
                Action = "ADD_DOWNLOADS",
                Urls = request.Urls,
                Referrer = request.Referrer,
                UserAgent = request.UserAgent,
                Cookie = request.Cookie
            };

            var bulkReply = await PipeClient.SendAsync(bulkMessage, TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            if (bulkReply is null)
            {
                LaunchApp(null);
                bulkReply = await PipeClient.SendAsync(bulkMessage, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
            }

            return bulkReply is null
                ? new NativeReply { Ok = false, Message = "ADM is not running and could not be started." }
                : new NativeReply { Ok = bulkReply.Ok, Message = bulkReply.Message, DownloadId = bulkReply.DownloadId };
        }

        if (action is "OPEN_VIDEO")
        {
            var videoMessage = new PipeMessage
            {
                Action = "OPEN_VIDEO",
                Url = request.Url,
                Referrer = request.Referrer,
                UserAgent = request.UserAgent
            };

            var videoReply = await PipeClient.SendAsync(videoMessage, TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            if (videoReply is null)
            {
                // Start ADM without arguments and let the pipe message open
                // the picker. Passing the URL would make the app act on it
                // twice, once at startup and once through the pipe.
                LaunchApp(null);
                videoReply = await PipeClient.SendAsync(videoMessage, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
            }

            return videoReply is null
                ? new NativeReply { Ok = false, Message = "ADM is not running and could not be started." }
                : new NativeReply { Ok = videoReply.Ok, Message = videoReply.Message };
        }

        var message = new PipeMessage
        {
            Action = "ADD_DOWNLOAD",
            Url = request.Url,
            FileName = request.FileName,
            Referrer = request.Referrer,
            UserAgent = request.UserAgent,
            Connections = request.Connections,
            SpeedLimitBytesPerSecond = request.SpeedLimitBytesPerSecond,
            Cookie = request.Cookie,
            Authorization = request.Authorization
        };

        var reply = await PipeClient.SendAsync(message, TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        if (reply is null)
        {
            LaunchApp(request.Url);
            reply = await PipeClient.SendAsync(message, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        }

        return reply is null
            ? new NativeReply { Ok = false, Message = "ADM is not running and could not be started." }
            : new NativeReply { Ok = reply.Ok, Message = reply.Message, DownloadId = reply.DownloadId };
    }

    private static void LaunchApp(string? url)
    {
        var executable = FindMainExecutable();
        if (executable is null)
            throw new FileNotFoundException("ADM.exe was not installed next to the native messaging host.");

        var startInfo = new ProcessStartInfo(executable) { UseShellExecute = true };
        if (!string.IsNullOrWhiteSpace(url))
            startInfo.ArgumentList.Add(url);

        Process.Start(startInfo);
    }

    private static string? FindMainExecutable()
    {
        // Allow developers to point at a build output without reinstalling.
        string? overridePath = Environment.GetEnvironmentVariable("ADM_EXE");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        // The installed layout puts the host in {app}\native and the app in
        // {app}, so the app is one or two levels up.
        string baseDirectory = AppContext.BaseDirectory;
        string[] installedCandidates =
        {
            Path.Combine(baseDirectory, "ADM.exe"),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "ADM.exe")),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "ADM.exe"))
        };

        foreach (string candidate in installedCandidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        // In a development checkout the app lives in a sibling project, so the
        // repository root is located and the known build outputs are checked.
        // This works without the ADM_EXE variable, which a browser that is
        // already running never inherits.
        foreach (string candidate in FindDevelopmentCandidates(baseDirectory))
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Looks for the desktop app in a development checkout by walking up to the
    /// folder that holds the solution file.
    /// </summary>
    private static IEnumerable<string> FindDevelopmentCandidates(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ADM.sln")))
            {
                string root = directory.FullName;
                yield return Path.Combine(root, "artifacts", "publish", "app", "ADM.exe");
                yield return Path.Combine(root, "src", "ADM.App", "bin", "Debug", "net8.0-windows", "ADM.exe");
                yield return Path.Combine(root, "src", "ADM.App", "bin", "Release", "net8.0-windows", "ADM.exe");
                yield return Path.Combine(root, "src", "ADM.App", "bin", "Release", "net8.0-windows", "win-x64", "ADM.exe");
                yield break;
            }

            directory = directory.Parent;
        }
    }
}
