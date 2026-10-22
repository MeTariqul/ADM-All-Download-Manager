using System.Text.Json;

namespace Adm.Core.Ipc;

/// <summary>Request sent by the browser extension through the native messaging host.</summary>
public sealed class NativeRequest
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

public sealed class NativeReply
{
    public bool Ok { get; set; }

    public string? Message { get; set; }

    public string? DownloadId { get; set; }
}

/// <summary>
/// Chrome/Edge native messaging framing: a 4-byte little-endian length followed
/// by that many bytes of UTF-8 JSON.
/// </summary>
public static class NativeMessagingProtocol
{
    public const int MaximumMessageBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<NativeRequest?> ReadAsync(Stream input, CancellationToken cancellationToken = default)
    {
        var lengthBuffer = new byte[4];
        await input.ReadExactlyAsync(lengthBuffer, cancellationToken).ConfigureAwait(false);

        int length = BitConverter.ToInt32(lengthBuffer, 0);
        if (length <= 0 || length > MaximumMessageBytes)
            throw new InvalidDataException($"Invalid native message length: {length}");

        var payload = new byte[length];
        await input.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Deserialize<NativeRequest>(payload, JsonOptions);
    }

    public static async Task WriteAsync(Stream output, object message, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        var lengthBuffer = BitConverter.GetBytes(payload.Length);

        await output.WriteAsync(lengthBuffer, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
