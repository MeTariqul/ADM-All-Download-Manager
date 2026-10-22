namespace Adm.Core.Engine;

/// <summary>
/// One contiguous byte range of a download. The owning worker updates
/// <see cref="Position"/>; a coordinator may shrink a running segment to hand
/// the second half to a new worker (dynamic segmentation).
/// </summary>
internal sealed class Segment
{
    private readonly object _sync = new();
    private long _end;
    private long _position;

    public Segment(long start, long end, long position)
    {
        Start = start;
        _end = end;
        _position = Math.Clamp(position, start, end < 0 ? long.MaxValue : end + 1);
    }

    public long Start { get; }

    /// <summary>Inclusive end offset, or -1 when the response has no known length.</summary>
    public long End
    {
        get
        {
            lock (_sync)
                return _end;
        }
    }

    /// <summary>Next byte offset to write. Equals <see cref="End"/> + 1 when finished.</summary>
    public long Position
    {
        get
        {
            lock (_sync)
                return _position;
        }
    }

    public long Remaining
    {
        get
        {
            lock (_sync)
                return _end < 0 ? long.MaxValue : Math.Max(0, _end - _position + 1);
        }
    }

    public bool IsComplete
    {
        get
        {
            lock (_sync)
                return _end >= 0 && _position > _end;
        }
    }

    public void SetPosition(long position)
    {
        lock (_sync)
        {
            if (position > _position)
                _position = position;
        }
    }

    /// <summary>
    /// Shrinks this segment to its first half and returns the previous end so the
    /// coordinator can create a second segment for the remainder.
    /// </summary>
    public long? TryShrink(long minimumRemaining)
    {
        lock (_sync)
        {
            if (_end < 0)
                return null;

            long remaining = _end - _position + 1;
            if (remaining < minimumRemaining)
                return null;

            long oldEnd = _end;
            _end = _position + remaining / 2;
            return oldEnd;
        }
    }
}
