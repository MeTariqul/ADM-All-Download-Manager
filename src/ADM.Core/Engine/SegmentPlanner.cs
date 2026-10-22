using Adm.Core.Models;

namespace Adm.Core.Engine;

/// <summary>Splits a known file size into byte ranges for parallel downloading.</summary>
public static class SegmentPlanner
{
    /// <summary>Segments smaller than this are not worth a separate connection.</summary>
    public const long MinimumSegmentSize = 512 * 1024;

    public const int MaximumConnections = 32;

    public static List<SegmentRecord> Plan(long totalBytes, int requestedConnections)
    {
        var segments = new List<SegmentRecord>();

        // Unknown length: one sequential stream, no resume support.
        if (totalBytes <= 0)
        {
            segments.Add(new SegmentRecord { Start = 0, End = -1, Position = 0 });
            return segments;
        }

        int connections = Math.Clamp(requestedConnections, 1, MaximumConnections);
        connections = (int)Math.Min(connections, Math.Max(1, totalBytes / MinimumSegmentSize));
        connections = Math.Max(1, connections);

        long baseSize = totalBytes / connections;
        long remainder = totalBytes % connections;

        long start = 0;
        for (int i = 0; i < connections; i++)
        {
            long size = baseSize + (i < remainder ? 1 : 0);
            long end = start + size - 1;
            segments.Add(new SegmentRecord { Start = start, End = end, Position = start });
            start = end + 1;
        }

        return segments;
    }
}
