using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Adm.Core.Ipc;

/// <summary>Message exchanged between the browser native host and the desktop app.</summary>
public sealed class PipeMessage
{
    public string? Action { get; set; }

    public string? Url { get; set; }

    public string? FileName { get; set; }

    public string? Referrer { get; set; }

    public string? UserAgent { get; set; }

    public int? Connections { get; set; }

    public long? SpeedLimitBytesPerSecond { get; set; }

    /// <summary>Cookie header forwarded from the browser.</summary>
    public string? Cookie { get; set; }

    /// <summary>Value for the Authorization header.</summary>
    public string? Authorization { get; set; }

    /// <summary>Several URLs separated by newlines, used by the bulk action.</summary>
    public string? Urls { get; set; }

    /// <summary>True when the extension should hand browser downloads to ADM.</summary>
    public bool? Intercept { get; set; }
}

public sealed class PipeReply
{
    public bool Ok { get; set; }

    public string? Message { get; set; }

    public string? DownloadId { get; set; }

    public int ActiveCount { get; set; }
}

public static class PipeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
}

/// <summary>Line-delimited JSON over a named pipe. Only one request per connection.</summary>
public static class PipeClient
{
    public const string PipeName = "ADM.DownloadManager.v1";

    public static async Task<PipeReply?> SendAsync(PipeMessage message, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(4));

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(500).ConfigureAwait(false);

                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
                {
                    AutoFlush = true
                };
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);

                await writer.WriteLineAsync(JsonSerializer.Serialize(message, PipeJson.Options)).ConfigureAwait(false);
                string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                return line is null ? null : JsonSerializer.Deserialize<PipeReply>(line, PipeJson.Options);
            }
            catch (TimeoutException)
            {
                await Task.Delay(120).ConfigureAwait(false);
            }
            catch (IOException)
            {
                await Task.Delay(120).ConfigureAwait(false);
            }
        }

        return null;
    }
}
