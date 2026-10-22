using System.Globalization;
using System.Text.Json;

namespace Adm.Core.Media;

/// <summary>One entry of a playlist, as reported by <c>yt-dlp --flat-playlist -J</c>.</summary>
public sealed class PlaylistEntry
{
    public string Title { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public string Id { get; init; } = string.Empty;

    public TimeSpan? Duration { get; init; }

    public string DurationText => Duration is { TotalSeconds: > 0 } duration
        ? duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}"
        : "";

    public override string ToString() =>
        string.IsNullOrWhiteSpace(DurationText) ? Title : $"{Title}  ·  {DurationText}";
}

/// <summary>A playlist and the videos it contains.</summary>
public sealed class PlaylistInfo
{
    public string Title { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public string Uploader { get; init; } = string.Empty;

    public IReadOnlyList<PlaylistEntry> Entries { get; init; } = Array.Empty<PlaylistEntry>();

    public int Count => Entries.Count;

    public string SummaryText =>
        $"{(string.IsNullOrWhiteSpace(Uploader) ? "Playlist" : Uploader)}  ·  {Count} videos";
}

/// <summary>Reads the flat playlist listing produced by yt-dlp.</summary>
public static class YtDlpPlaylistParser
{
    public static PlaylistInfo Parse(string json, string fallbackUrl)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new YtDlpException("yt-dlp returned an empty playlist.");

        try
        {
            using var document = JsonDocument.Parse(json.Trim().TrimStart('\uFEFF'));
            return Parse(document.RootElement, fallbackUrl);
        }
        catch (JsonException ex)
        {
            throw new YtDlpException("yt-dlp returned an unexpected playlist response.", ex);
        }
    }

    private static PlaylistInfo Parse(JsonElement root, string fallbackUrl)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new YtDlpException("yt-dlp returned an unexpected playlist response.");

        var entries = new List<PlaylistEntry>();

        if (root.TryGetProperty("entries", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in list.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;

                string title = GetString(entry, "title");
                string url = GetString(entry, "url");
                string id = GetString(entry, "id");

                if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(id))
                    continue;

                if (string.IsNullOrWhiteSpace(url))
                    url = id;

                double seconds = GetDouble(entry, "duration");

                entries.Add(new PlaylistEntry
                {
                    Title = string.IsNullOrWhiteSpace(title) ? id : title,
                    Url = url,
                    Id = id,
                    Duration = seconds > 0 ? TimeSpan.FromSeconds(seconds) : null
                });
            }
        }

        if (entries.Count == 0)
            throw new YtDlpException("The playlist does not contain any downloadable videos.");

        string playlistTitle = GetString(root, "title");
        string uploader = GetString(root, "uploader");
        if (string.IsNullOrWhiteSpace(uploader))
            uploader = GetString(root, "channel");

        string webpage = GetString(root, "webpage_url");

        return new PlaylistInfo
        {
            Title = string.IsNullOrWhiteSpace(playlistTitle) ? "Playlist" : playlistTitle,
            Url = string.IsNullOrWhiteSpace(webpage) ? fallbackUrl : webpage,
            Uploader = uploader,
            Entries = entries
        };
    }

    private static string GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return string.Empty;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty
        };
    }

    private static double GetDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return 0;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number))
            return number;

        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return 0;
    }
}
