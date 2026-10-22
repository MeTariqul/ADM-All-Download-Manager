using System.Diagnostics;

namespace Adm.Core.Engine;

/// <summary>
/// Token bucket shared by every worker of a download. Workers ask for the bytes
/// they just read; when the bucket is empty they wait, which applies TCP
/// back-pressure to the server. A limit of 0 disables throttling.
/// </summary>
public sealed class SpeedLimiter
{
    private const long BurstBytes = 128 * 1024;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _bytesPerSecond;
    private double _tokens = BurstBytes;
    private double _lastRefillSeconds;

    public SpeedLimiter(long bytesPerSecond)
    {
        _bytesPerSecond = Math.Max(0, bytesPerSecond);
        _lastRefillSeconds = _clock.Elapsed.TotalSeconds;
    }

    public long BytesPerSecond => Interlocked.Read(ref _bytesPerSecond);

    public bool IsEnabled => BytesPerSecond > 0;

    public void UpdateLimit(long bytesPerSecond)
    {
        Interlocked.Exchange(ref _bytesPerSecond, Math.Max(0, bytesPerSecond));
    }

    public async ValueTask ThrottleAsync(int byteCount, CancellationToken cancellationToken)
    {
        long limit = BytesPerSecond;
        if (limit <= 0 || byteCount <= 0)
            return;

        while (true)
        {
            TimeSpan wait;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                limit = BytesPerSecond;
                if (limit <= 0)
                    return;

                double now = _clock.Elapsed.TotalSeconds;
                double burst = Math.Min(BurstBytes, limit);
                _tokens = Math.Min(burst, _tokens + (now - _lastRefillSeconds) * limit);
                _lastRefillSeconds = now;

                if (_tokens >= byteCount)
                {
                    _tokens -= byteCount;
                    return;
                }

                wait = TimeSpan.FromSeconds((byteCount - _tokens) / limit);
            }
            finally
            {
                _gate.Release();
            }

            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }
}
