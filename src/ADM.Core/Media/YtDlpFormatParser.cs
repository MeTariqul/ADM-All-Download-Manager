using System.Globalization;
using System.Text.Json;

namespace Adm.Core.Media;

/// <summary>
/// Converts the JSON emitted by <c>yt-dlp -J</c> into a short, human-friendly
/// list of resolutions. Video-only formats are paired with the best audio so a
/// single picker row can produce a complete MP4/MKV file.
/// </summary>
public static class YtDlpFormatParser
{
    private const int MaximumOptions = 22;

    public static VideoInfo Parse(string json, string fallbackUrl, bool allowPlaylist = false)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new YtDlpException("yt-dlp returned an empty response.");

        try
        {
            using var document = JsonDocument.Parse(json.Trim().TrimStart('\uFEFF'));
            return Parse(document.RootElement, fallbackUrl, allowPlaylist);
        }
        catch (JsonException ex)
        {
            throw new YtDlpException("yt-dlp returned an unexpected response.", ex);
        }
    }

    private static VideoInfo Parse(JsonElement root, string fallbackUrl, bool allowPlaylist)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new YtDlpException("yt-dlp returned an unexpected response.");

        string type = GetString(root, "_type");
        bool isPlaylist = type is "playlist" or "multi_video" ||
            (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array &&
             entries.GetArrayLength() > 1);

        if (isPlaylist)
        {
            if (!allowPlaylist)
                throw new YtDlpException("This link is a playlist. Open a single video and try again.");

            // The flat listing has no usable formats, so the playlist is
            // returned on its own and the picker offers a separate flow.
            var playlist = YtDlpPlaylistParser.Parse(root.GetRawText(), fallbackUrl);

            return new VideoInfo
            {
                PageUrl = playlist.Url,
                Id = "playlist",
                Title = playlist.Title,
                Uploader = string.IsNullOrWhiteSpace(playlist.Uploader) ? null : playlist.Uploader,
                Formats = Array.Empty<VideoFormat>(),
                Playlist = playlist
            };
        }

        string liveStatus = GetString(root, "live_status");
        if (GetBool(root, "is_live") || liveStatus is "is_live" or "is_upcoming")
            throw new YtDlpException("This is a live stream. Live streams cannot be downloaded yet.");

        string title = GetString(root, "title");
        string id = GetString(root, "id");
        string pageUrl = GetString(root, "webpage_url");
        string uploader = GetString(root, "uploader");
        if (string.IsNullOrWhiteSpace(uploader))
            uploader = GetString(root, "channel");
        string thumbnail = GetString(root, "thumbnail");

        double durationSeconds = GetDouble(root, "duration");
        TimeSpan? duration = durationSeconds > 0
            ? TimeSpan.FromSeconds(durationSeconds)
            : null;

        var parsedFormats = ReadFormats(root);
        var formats = BuildOptions(parsedFormats);

        if (formats.Count == 0)
            throw new YtDlpException("No downloadable video or audio formats were found.");

        if (string.IsNullOrWhiteSpace(title))
            title = string.IsNullOrWhiteSpace(id) ? "Video" : id;
        if (string.IsNullOrWhiteSpace(pageUrl))
            pageUrl = fallbackUrl;
        if (string.IsNullOrWhiteSpace(id))
            id = "video";

        return new VideoInfo
        {
            PageUrl = pageUrl,
            Id = id,
            Title = title.Trim(),
            Uploader = string.IsNullOrWhiteSpace(uploader) ? null : uploader.Trim(),
            Duration = duration,
            ThumbnailUrl = string.IsNullOrWhiteSpace(thumbnail) ? null : thumbnail,
            Formats = formats
        };
    }

    private static List<VideoFormat> BuildOptions(List<FormatEntry> entries)
    {
        var options = new List<VideoFormat>(MaximumOptions);
        var audioEntry = PickBestAudio(entries);
        long audioBytes = audioEntry?.Bytes ?? 0;

        var videoEntries = entries
            .Where(entry => entry.HasVideo && entry.Height > 0)
            .GroupBy(entry => entry.Height)
            .OrderByDescending(group => group.Key)
            .ToList();

        // Prefer direct HTTPS streams. HLS/DASH manifests often report wildly
        // inflated sizes and duplicate every resolution.
        foreach (var heightGroup in videoEntries)
        {
            var pool = heightGroup.Where(entry => entry.IsDirect).ToList();
            if (pool.Count == 0)
                pool = heightGroup.ToList();

            var mp4 = pool
                .Where(entry => string.Equals(entry.Extension, "mp4", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(ScoreVideo)
                .FirstOrDefault();

            var other = pool
                .Where(entry => !string.Equals(entry.Extension, "mp4", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(ScoreVideo)
                .FirstOrDefault();

            if (mp4 is not null)
                options.Add(BuildVideoOption(mp4, audioEntry, audioBytes, options.Count == 0));
            if (other is not null && options.Count < MaximumOptions)
                options.Add(BuildVideoOption(other, audioEntry, audioBytes, options.Count == 0));

            if (options.Count >= MaximumOptions)
                break;
        }

        // Some extractors only expose a single "best" format with no height.
        if (options.Count == 0)
        {
            var fallback = entries.Where(entry => entry.HasVideo).OrderByDescending(ScoreVideo).FirstOrDefault();
            if (fallback is not null)
            {
                bool hasAudio = fallback.HasAudio;
                string container = hasAudio
                    ? NormalizeExtension(fallback.Extension)
                    : (string.Equals(fallback.Extension, "mp4", StringComparison.OrdinalIgnoreCase) ? "mp4" : "mkv");

                options.Add(new VideoFormat
                {
                    Selector = hasAudio
                        ? fallback.Id
                        : $"{fallback.Id}+bestaudio[ext=m4a]/{fallback.Id}+bestaudio/best",
                    ResolutionText = "Best available",
                    FormatText = FormatText(container, fallback),
                    Extension = container,
                    MergeContainer = hasAudio ? null : container,
                    EstimatedBytes = Estimate(fallback, audioEntry, audioBytes),
                    IsRecommended = true
                });
            }
        }

        if (audioEntry is not null)
        {
            options.Add(new VideoFormat
            {
                Selector = string.Equals(audioEntry.Extension, "m4a", StringComparison.OrdinalIgnoreCase)
                    ? "bestaudio[ext=m4a]/bestaudio"
                    : "bestaudio/best",
                ResolutionText = "Audio only",
                FormatText = $"M4A · {BitrateText(audioEntry)}",
                Extension = "m4a",
                EstimatedBytes = audioEntry.Bytes,
                IsAudioOnly = true,
                IsRecommended = options.Count == 0
            });

            options.Add(new VideoFormat
            {
                Selector = "bestaudio/best",
                ResolutionText = "Audio only",
                FormatText = "MP3 · high quality",
                Extension = "mp3",
                EstimatedBytes = audioEntry.Bytes,
                IsAudioOnly = true,
                ConvertToMp3 = true,
                IsRecommended = options.Count == 0
            });
        }

        return options;
    }

    private static VideoFormat BuildVideoOption(
        FormatEntry entry,
        FormatEntry? audioEntry,
        long audioBytes,
        bool recommended)
    {
        bool hasAudio = entry.HasAudio;
        string container = hasAudio
            ? NormalizeExtension(entry.Extension)
            : (string.Equals(entry.Extension, "mp4", StringComparison.OrdinalIgnoreCase) ? "mp4" : "mkv");

        string selector = hasAudio
            ? entry.Id
            : $"{entry.Id}+bestaudio[ext=m4a]/{entry.Id}+bestaudio/best";

        return new VideoFormat
        {
            Selector = selector,
            ResolutionText = ResolutionText(entry),
            FormatText = FormatText(container, entry),
            Extension = container,
            MergeContainer = hasAudio ? null : container,
            EstimatedBytes = Estimate(entry, audioEntry, audioBytes),
            IsRecommended = recommended
        };
    }

    private static long Estimate(FormatEntry video, FormatEntry? audio, long audioBytes)
    {
        long total = Math.Max(0, video.Bytes);
        if (!video.HasAudio && total > 0 && audioBytes > 0)
            total += audioBytes;
        return total;
    }

    private static string ResolutionText(FormatEntry entry)
    {
        string text = $"{entry.Height}p";
        if (entry.Fps > 30.5)
            text += ((int)Math.Round(entry.Fps)).ToString(CultureInfo.InvariantCulture);
        return text;
    }

    private static string FormatText(string container, FormatEntry entry)
    {
        string text = $"{container.ToUpperInvariant()} · {CodecName(entry.VideoCodec)}";
        if (entry.Fps > 30.5)
            text += $" · {entry.Fps:0} fps";
        return text;
    }

    private static string NormalizeExtension(string extension)
    {
        extension = (extension ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
        return extension is "mp4" or "m4v"
            ? "mp4"
            : extension is "webm" or "mkv" or "mov" or "flv" or "avi"
                ? (extension == "webm" ? "webm" : extension == "mkv" ? "mkv" : "mkv")
                : "mkv";
    }

    private static string CodecName(string codec)
    {
        string value = (codec ?? string.Empty).ToLowerInvariant();
        if (value.StartsWith("avc", StringComparison.Ordinal) || value.Contains("h264", StringComparison.Ordinal))
            return "H.264";
        if (value.StartsWith("av01", StringComparison.Ordinal) || value.Contains("av1", StringComparison.Ordinal))
            return "AV1";
        if (value.StartsWith("vp09", StringComparison.Ordinal) || value.StartsWith("vp9", StringComparison.Ordinal))
            return "VP9";
        if (value.StartsWith("hev", StringComparison.Ordinal) || value.StartsWith("hvc", StringComparison.Ordinal))
            return "HEVC";
        return string.IsNullOrWhiteSpace(value) || value == "none"
            ? "Video"
            : value.Split('.')[0].ToUpperInvariant();
    }

    private static string BitrateText(FormatEntry entry)
    {
        double bitrate = entry.Abr > 0 ? entry.Abr : entry.Tbr;
        return bitrate > 0 ? $"{bitrate:0} kbps" : "audio";
    }

    private static double ScoreVideo(FormatEntry entry)
    {
        double score = 0;

        if (entry.IsDirect)
            score += 100;
        if (string.Equals(entry.Extension, "mp4", StringComparison.OrdinalIgnoreCase))
            score += 20;

        string codec = entry.VideoCodec.ToLowerInvariant();
        if (codec.StartsWith("avc", StringComparison.Ordinal) || codec.Contains("h264", StringComparison.Ordinal))
            score += 15;
        else if (codec.StartsWith("av01", StringComparison.Ordinal))
            score += 10;
        else if (codec.StartsWith("vp09", StringComparison.Ordinal) || codec.StartsWith("vp9", StringComparison.Ordinal))
            score += 8;

        if (entry.HasAudio)
            score += 12;

        score += Math.Min(10, entry.Tbr / 1000d);
        if (entry.Fps > 30.5)
            score += 2;
        if (entry.Bytes > 0)
            score += 1;

        return score;
    }

    private static FormatEntry? PickBestAudio(List<FormatEntry> entries)
    {
        var audio = entries.Where(entry => entry.HasAudio && !entry.HasVideo).ToList();
        if (audio.Count == 0)
            audio = entries.Where(entry => entry.HasAudio).ToList();
        if (audio.Count == 0)
            return null;

        var direct = audio.Where(entry => entry.IsDirect).ToList();
        if (direct.Count > 0)
            audio = direct;

        return audio
            .OrderByDescending(entry => string.Equals(entry.Extension, "m4a", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(entry => entry.Abr > 0 ? entry.Abr : entry.Tbr)
            .ThenByDescending(entry => entry.Bytes)
            .FirstOrDefault();
    }

    private static List<FormatEntry> ReadFormats(JsonElement root)
    {
        var result = new List<FormatEntry>();
        if (!root.TryGetProperty("formats", out var formats) || formats.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in formats.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            string id = GetString(item, "format_id");
            if (string.IsNullOrWhiteSpace(id))
                continue;

            string extension = GetString(item, "ext");
            string videoCodec = GetString(item, "vcodec");
            string audioCodec = GetString(item, "acodec");
            string note = GetString(item, "format_note");

            if (string.Equals(extension, "mhtml", StringComparison.OrdinalIgnoreCase) ||
                note.Contains("storyboard", StringComparison.OrdinalIgnoreCase) ||
                GetBool(item, "has_drm"))
            {
                continue;
            }

            bool hasVideo = HasCodec(videoCodec);
            bool hasAudio = HasCodec(audioCodec);
            if (!hasVideo && !hasAudio)
                continue;

            result.Add(new FormatEntry
            {
                Id = id,
                Extension = extension,
                VideoCodec = videoCodec,
                AudioCodec = audioCodec,
                Protocol = GetString(item, "protocol"),
                Height = (int)Math.Clamp(GetLong(item, "height"), 0, 100_000),
                Fps = GetDouble(item, "fps"),
                Bytes = Math.Max(GetLong(item, "filesize"), GetLong(item, "filesize_approx")),
                Tbr = GetDouble(item, "tbr"),
                Abr = GetDouble(item, "abr")
            });
        }

        return result;
    }

    private static bool HasCodec(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !string.Equals(value, "none", StringComparison.OrdinalIgnoreCase);

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

    private static long GetLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return 0;

        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out long number))
                return number;
            if (value.TryGetDouble(out double floating))
                return (long)floating;
        }

        if (value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long stringNumber))
        {
            return stringNumber;
        }

        return 0;
    }

    private static bool GetBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return false;

        return value.ValueKind == JsonValueKind.True ||
               (value.ValueKind == JsonValueKind.String &&
                bool.TryParse(value.GetString(), out bool parsed) && parsed);
    }

    private sealed class FormatEntry
    {
        public string Id { get; init; } = string.Empty;
        public string Extension { get; init; } = string.Empty;
        public string VideoCodec { get; init; } = string.Empty;
        public string AudioCodec { get; init; } = string.Empty;
        public string Protocol { get; init; } = string.Empty;
        public int Height { get; init; }
        public double Fps { get; init; }
        public long Bytes { get; init; }
        public double Tbr { get; init; }
        public double Abr { get; init; }

        public bool HasVideo => !string.IsNullOrWhiteSpace(VideoCodec) &&
                                !string.Equals(VideoCodec, "none", StringComparison.OrdinalIgnoreCase);

        public bool HasAudio => !string.IsNullOrWhiteSpace(AudioCodec) &&
                                !string.Equals(AudioCodec, "none", StringComparison.OrdinalIgnoreCase);

        public bool IsDirect => Protocol.StartsWith("http", StringComparison.OrdinalIgnoreCase);
    }
}
