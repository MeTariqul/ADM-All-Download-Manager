namespace Adm.Core.Media;

/// <summary>Metadata and selectable formats returned by a yt-dlp probe.</summary>
public sealed class VideoInfo
{
    public string PageUrl { get; init; } = string.Empty;

    public string Id { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string? Uploader { get; init; }

    public TimeSpan? Duration { get; init; }

    public string? ThumbnailUrl { get; init; }

    public IReadOnlyList<VideoFormat> Formats { get; init; } = Array.Empty<VideoFormat>();

    /// <summary>Set when the page turned out to be a playlist rather than a single video.</summary>
    public PlaylistInfo? Playlist { get; init; }

    public bool IsPlaylist => Playlist is not null;

    public string DurationText => Duration is { TotalSeconds: > 0 } duration
        ? duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}"
        : string.Empty;

    public string SummaryText
    {
        get
        {
            var parts = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(Uploader))
                parts.Add(Uploader!);
            if (!string.IsNullOrWhiteSpace(DurationText))
                parts.Add(DurationText);
            if (Formats.Count > 0)
                parts.Add(Formats.Count == 1 ? "1 format" : $"{Formats.Count} formats");
            return string.Join("  ·  ", parts);
        }
    }
}
