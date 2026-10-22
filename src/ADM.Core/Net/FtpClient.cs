using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Adm.Core.Util;

namespace Adm.Core.Net;

/// <summary>
/// A small FTP client built on sockets. It supports implicit TLS, passive
/// transfers and resuming through the REST command.
/// </summary>
public sealed class FtpClient : IDisposable
{
    private const int BufferSize = 64 * 1024;

    private readonly TcpClient _control = new();
    private Stream? _controlStream;
    private SslStream? _sslStream;
    private bool _disposed;

    public FtpClient(string host, int port, bool secure, string? userName, string? password)
    {
        Host = host;
        Port = port;
        IsSecure = secure;
        UserName = userName;
        Password = password;
    }

    public string Host { get; }

    public int Port { get; }

    public bool IsSecure { get; }

    public string? UserName { get; }

    public string? Password { get; }

    /// <summary>Connects, logs in and switches to binary mode.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _control.ConnectAsync(Host, Port, cancellationToken).ConfigureAwait(false);
        _control.NoDelay = true;

        var network = _control.GetStream();

        if (IsSecure)
        {
            // Implicit TLS: the whole session is encrypted from the start.
            _sslStream = new SslStream(network, leaveInnerStreamOpen: false);
            await _sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = Host
            }, cancellationToken).ConfigureAwait(false);

            _controlStream = _sslStream;
        }
        else
        {
            _controlStream = network;
        }

        await ExpectAsync("220", cancellationToken).ConfigureAwait(false);
        await SendAsync($"USER {UserName ?? "anonymous"}", cancellationToken).ConfigureAwait(false);
        await ExpectAsync("230", "331", cancellationToken).ConfigureAwait(false);

        if (LastReply.StartsWith("331", StringComparison.Ordinal))
        {
            await SendAsync($"PASS {Password ?? "adm@localhost"}", cancellationToken).ConfigureAwait(false);
            await ExpectAsync("230", cancellationToken).ConfigureAwait(false);
        }

        await SendAsync("TYPE I", cancellationToken).ConfigureAwait(false);
        await ExpectAsync("200", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads a file into the given stream, starting at the given offset.
    /// Returns the number of bytes that were written.
    /// </summary>
    public async Task<long> DownloadAsync(string path, Stream destination, long offset,
        Action<long>? onProgress, CancellationToken cancellationToken = default)
    {
        (string host, int port) = await EnterPassiveModeAsync(cancellationToken).ConfigureAwait(false);

        using var data = new TcpClient();
        await data.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        data.NoDelay = true;

        using var dataStream = data.GetStream();

        if (offset > 0)
        {
            await SendAsync($"REST {offset}", cancellationToken).ConfigureAwait(false);
            await ExpectAsync("350", cancellationToken).ConfigureAwait(false);
        }

        await SendAsync($"RETR {path}", cancellationToken).ConfigureAwait(false);
        await ExpectAsync("150", "125", cancellationToken).ConfigureAwait(false);

        long written = 0;
        var buffer = new byte[BufferSize];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int read = await dataStream.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)
                .ConfigureAwait(false);

            if (read <= 0)
                break;

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            written += read;
            onProgress?.Invoke(written);
        }

        await ExpectAsync("226", "250", cancellationToken).ConfigureAwait(false);
        return written;
    }

    /// <summary>Asks the server for the size of a file, or -1 when it does not know.</summary>
    public async Task<long> GetSizeAsync(string path, CancellationToken cancellationToken = default)
    {
        await SendAsync($"SIZE {path}", cancellationToken).ConfigureAwait(false);
        string reply = await ReadReplyAsync(cancellationToken).ConfigureAwait(false);

        if (!reply.StartsWith("213", StringComparison.Ordinal))
            return -1;

        string[] parts = reply.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return -1;

        return long.TryParse(parts[1], out long size) ? size : -1;
    }

    /// <summary>Opens the passive data channel and returns its address.</summary>
    private async Task<(string Host, int Port)> EnterPassiveModeAsync(CancellationToken cancellationToken)
    {
        await SendAsync("PASV", cancellationToken).ConfigureAwait(false);
        string reply = await ExpectAsync("227", cancellationToken).ConfigureAwait(false);

        int open = reply.IndexOf('(');
        int close = reply.IndexOf(')', open + 1);
        if (open < 0 || close < 0)
            throw new IOException("The server did not return a passive address: " + reply);

        string[] parts = reply[(open + 1)..close].Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 6)
            throw new IOException("The server returned an unusable passive address: " + reply);

        string host = string.Join('.', parts.Take(4));
        int port = (int.Parse(parts[4], System.Globalization.CultureInfo.InvariantCulture) << 8) +
                   int.Parse(parts[5], System.Globalization.CultureInfo.InvariantCulture);

        return (host, port);
    }

    private string LastReply { get; set; } = string.Empty;

    private async Task SendAsync(string command, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(command + "\r\n");
        await _controlStream!.WriteAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _controlStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ReadReplyAsync(CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new byte[1];

        while (true)
        {
            int read = await _controlStream!.ReadAsync(buffer.AsMemory(0, 1), cancellationToken)
                .ConfigureAwait(false);

            if (read <= 0)
                throw new IOException("The server closed the control connection.");

            char ch = (char)buffer[0];
            builder.Append(ch);

            if (ch == '\n')
                break;
        }

        LastReply = builder.ToString().TrimEnd('\r', '\n');
        return LastReply;
    }

    private async Task<string> ExpectAsync(string expected, CancellationToken cancellationToken) =>
        await ExpectAsync(expected, expected, cancellationToken).ConfigureAwait(false);

    private async Task<string> ExpectAsync(string first, string second, CancellationToken cancellationToken)
    {
        string reply = await ReadReplyAsync(cancellationToken).ConfigureAwait(false);

        if (reply.StartsWith(first, StringComparison.Ordinal) || reply.StartsWith(second, StringComparison.Ordinal))
            return reply;

        throw new IOException($"The server replied: {reply}");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try { _sslStream?.Dispose(); } catch (Exception) { /* closing anyway */ }
        try { _controlStream?.Dispose(); } catch (Exception) { /* closing anyway */ }
        try { _control.Dispose(); } catch (Exception) { /* closing anyway */ }
    }
}
