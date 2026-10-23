using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Adm.Core.Tests;

/// <summary>
/// Minimal HTTP/1.1 server used to exercise the download engine without the
/// internet. Supports Range requests, optional throttling and a mode that
/// deliberately ignores Range headers.
/// </summary>
internal sealed class RangeTestServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly byte[] _payload;
    private readonly bool _supportRanges;
    private readonly int _throttleMs;
    private readonly int _chunkSize;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly object _rangeLock = new();
    private readonly List<(long Start, long End)> _ranges = new();

    private int _requestCount;
    private int _rangeRequestCount;

    public RangeTestServer(byte[] payload, bool supportRanges = true, int throttleMs = 0, int chunkSize = 32 * 1024)
    {
        _payload = payload;
        _supportRanges = supportRanges;
        _throttleMs = throttleMs;
        _chunkSize = chunkSize;

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public string ContentDisposition { get; set; } = "attachment; filename=\"server-name.bin\"";

    public int ContentRangeOffset { get; set; }

    public int RequestCount => Volatile.Read(ref _requestCount);

    public int RangeRequestCount => Volatile.Read(ref _rangeRequestCount);

    public IReadOnlyList<(long Start, long End)> Ranges
    {
        get
        {
            lock (_rangeLock)
                return _ranges.ToList();
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                break;
            }

            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 8192, leaveOpen: true);

                string? requestLine = await reader.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(requestLine))
                    return;

                string? rangeHeader = null;
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync().ConfigureAwait(false)))
                {
                    if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                        rangeHeader = line["Range:".Length..].Trim();
                }

                Interlocked.Increment(ref _requestCount);

                long start = 0;
                long end = _payload.Length - 1;
                bool isRanged = false;

                if (_supportRanges && rangeHeader is not null && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                {
                    string spec = rangeHeader["bytes=".Length..].Split(',')[0].Trim();
                    int dash = spec.IndexOf('-');
                    if (dash > 0 && long.TryParse(spec[..dash], out long parsedStart))
                    {
                        start = parsedStart;
                        string endText = spec[(dash + 1)..];
                        if (!string.IsNullOrEmpty(endText) && long.TryParse(endText, out long parsedEnd))
                            end = Math.Min(parsedEnd, _payload.Length - 1);
                        else
                            end = _payload.Length - 1;

                        isRanged = true;
                    }
                }

                if (start >= _payload.Length && _supportRanges)
                {
                    string notSatisfiable =
                        "HTTP/1.1 416 Range Not Satisfiable\r\n" +
                        $"Content-Range: bytes */{_payload.Length}\r\n" +
                        "Content-Length: 0\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(notSatisfiable)).ConfigureAwait(false);
                    return;
                }

                long contentLength = end - start + 1;
                var headers = new StringBuilder();
                headers.Append(isRanged ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
                headers.Append("Content-Type: application/octet-stream\r\n");
                headers.Append("ETag: \"v1\"\r\n");
                headers.Append("Connection: close\r\n");
                if (_supportRanges)
                    headers.Append("Accept-Ranges: bytes\r\n");
                if (isRanged)
                {
                    int rangeOffset = end > start ? ContentRangeOffset : 0;
                    headers.Append($"Content-Range: bytes {start + rangeOffset}-{end + rangeOffset}/{_payload.Length + rangeOffset}\r\n");
                    Interlocked.Increment(ref _rangeRequestCount);
                    lock (_rangeLock)
                        _ranges.Add((start, end));
                }

                if (!string.IsNullOrEmpty(ContentDisposition))
                    headers.Append($"Content-Disposition: {ContentDisposition}\r\n");

                headers.Append($"Content-Length: {contentLength}\r\n\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString())).ConfigureAwait(false);

                long offset = start;
                while (offset <= end)
                {
                    int count = (int)Math.Min(_chunkSize, end - offset + 1);
                    await stream.WriteAsync(_payload.AsMemory((int)offset, count), _cts.Token).ConfigureAwait(false);
                    offset += count;

                    if (_throttleMs > 0)
                        await Task.Delay(_throttleMs, _cts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Clients (and the engine) close connections on purpose; that's expected.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();

        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // ignored
        }

        _cts.Dispose();
    }
}
