using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using Adm.Core.Net;

namespace Adm.Core.Engine;

internal sealed record TransferContext(
    HttpClient Client,
    string Url,
    string TempPath,
    string? UserAgent,
    string? Referrer,
    RangeConditionHeaderValue? IfRange,
    SpeedLimiter Limiter,
    int MaxRetries,
    string? Authorization = null,
    string? Cookie = null);

/// <summary>
/// Downloads a single <see cref="Segment"/> with HTTP Range requests, retrying
/// transient failures from the last written byte.
/// </summary>
internal static class SegmentWorker
{
    private const int BufferSize = 64 * 1024;

    public static async Task RunAsync(Segment segment, TransferContext context, CancellationToken cancellationToken)
    {
        int attempt = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool streaming = segment.End < 0;
            if (!streaming && segment.IsComplete)
                return;

            try
            {
                bool restart = streaming && attempt > 0;
                await TransferAsync(segment, context, streaming, restart, cancellationToken).ConfigureAwait(false);

                if (streaming || segment.IsComplete)
                    return;

                throw new IOException("The server closed the connection before this segment finished.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception) when (attempt < context.MaxRetries && !cancellationToken.IsCancellationRequested)
            {
                attempt++;
                var delay = TimeSpan.FromSeconds(Math.Min(10, Math.Pow(2, attempt)));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task TransferAsync(Segment segment, TransferContext context, bool streaming, bool restart,
        CancellationToken cancellationToken)
    {
        if (streaming && restart)
        {
            segment.SetPosition(0);
            using (var reset = new FileStream(context.TempPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                reset.SetLength(0);
        }

        long start = streaming ? 0 : segment.Position;
        long end = segment.End;

        if (!streaming && start > end)
            return;

        using var request = HttpProbe.CreateRequest(context.Url, context.UserAgent, context.Referrer,
            streaming ? null : new RangeHeaderValue(start, end), streaming ? null : context.IfRange,
            context.Authorization, context.Cookie);

        using var response = await context.Client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (streaming)
        {
            if (!response.IsSuccessStatusCode)
                response.EnsureSuccessStatusCode();
        }
        else
        {
            if (response.StatusCode == HttpStatusCode.OK)
                throw new InvalidOperationException(
                    "The server ignored the Range header. Restart the download to use a single connection.");

            if (response.StatusCode != HttpStatusCode.PartialContent)
                response.EnsureSuccessStatusCode();
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var file = new FileStream(context.TempPath, FileMode.OpenOrCreate, FileAccess.Write,
            FileShare.ReadWrite, BufferSize, FileOptions.Asynchronous);

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long position = start;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int maxRead = BufferSize;
                if (!streaming)
                {
                    long currentEnd = segment.End;
                    if (position > currentEnd)
                        break;

                    maxRead = (int)Math.Min(BufferSize, currentEnd - position + 1);
                }

                int read = await source.ReadAsync(buffer.AsMemory(0, maxRead), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                    break;

                await context.Limiter.ThrottleAsync(read, cancellationToken).ConfigureAwait(false);

                file.Position = position;
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

                position += read;
                segment.SetPosition(position);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
