namespace Adm.Core.Models;

/// <summary>Persisted byte range of one worker. <see cref="End"/> is inclusive.</summary>
public sealed class SegmentRecord
{
    public long Start { get; set; }

    public long End { get; set; }

    public long Position { get; set; }
}
