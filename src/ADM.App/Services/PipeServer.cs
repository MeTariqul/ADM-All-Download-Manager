using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Adm.Core.Ipc;

namespace Adm.App.Services;

/// <summary>
/// Accepts one request per connection over a local named pipe. The browser
/// native host and secondary app instances use this channel.
/// </summary>
public sealed class PipeServer : IDisposable
{
    private readonly Func<PipeMessage, PipeReply> _handler;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _loop;
    private bool _disposed;

    public PipeServer(Func<PipeMessage, PipeReply> handler)
    {
        _handler = handler;
    }

    public void Start()
    {
        _loop ??= LoopAsync();
    }

    private async Task LoopAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(
                    PipeClient.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
            }
            catch (Exception)
            {
                await Task.Delay(500, _cancellation.Token).ConfigureAwait(false);
                continue;
            }

            try
            {
                await server.WaitForConnectionAsync(_cancellation.Token).ConfigureAwait(false);
                _ = HandleAsync(server);
            }
            catch (OperationCanceledException)
            {
                server.Dispose();
                break;
            }
            catch (Exception)
            {
                server.Dispose();
                await Task.Delay(200, _cancellation.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream stream)
    {
        await using (stream)
        {
            try
            {
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true)
                {
                    AutoFlush = true
                };

                string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line))
                    return;

                PipeReply reply;
                try
                {
                    var message = JsonSerializer.Deserialize<PipeMessage>(line, PipeJson.Options) ?? new PipeMessage();
                    reply = _handler(message);
                }
                catch (Exception ex)
                {
                    reply = new PipeReply { Ok = false, Message = ex.Message };
                }

                await writer.WriteLineAsync(JsonSerializer.Serialize(reply, PipeJson.Options)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The client disconnected; nothing to report.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cancellation.Cancel();

        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (Exception)
        {
            // Ignore shutdown races.
        }

        _cancellation.Dispose();
    }
}
