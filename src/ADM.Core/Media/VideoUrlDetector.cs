namespace Adm.Core.Media;

/// <summary>
/// Recognises links that should open the video format picker instead of being
/// handed to the segmented HTTP engine.
/// </summary>
public static class VideoUrlDetector
{
    private static readonly string[] KnownHosts =
    {
        "youtube.com", "youtu.be", "vimeo.com", "dailymotion.com", "dai.ly",
        "twitch.tv", "twitter.com", "x.com", "facebook.com", "fb.watch",
        "instagram.com", "tiktok.com", "soundcloud.com", "bilibili.com",
        "reddit.com", "redd.it", "streamable.com", "rumble.com", "odysee.com"
    };

    private static readonly string[] MediaExtensions =
    {
        ".mp4", ".webm", ".mkv", ".mov", ".m4v", ".avi", ".flv", ".ts",
        ".mp3", ".m4a", ".aac", ".flac", ".wav", ".ogg", ".opus"
    };

    /// <summary>True when the URL belongs to a site the video engine supports.</summary>
    public static bool IsKnownVideoSite(string? url)
    {
        if (!TryParse(url, out var uri))
            return false;

        string host = uri.Host.ToLowerInvariant();
        return host == "youtu.be" ||
               KnownHosts.Any(known => host == known || host.EndsWith("." + known, StringComparison.Ordinal));
    }

    /// <summary>True when the URL points straight at a media file.</summary>
    public static bool IsDirectMediaFile(string? url)
    {
        if (!TryParse(url, out var uri))
            return false;

        string path = uri.AbsolutePath;
        string extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension))
            return false;

        return MediaExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True when the URL points at a likely single video page on a supported site.</summary>
    public static bool IsVideoPage(string? url)
    {
        if (!TryParse(url, out var uri))
            return false;

        string host = uri.Host.ToLowerInvariant();
        string path = uri.AbsolutePath;

        if (host == "youtu.be")
            return path.Length > 1;

        if (IsHost(host, "youtube.com"))
        {
            return path.StartsWith("/watch", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("/shorts/", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("/live/", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("/embed/", StringComparison.OrdinalIgnoreCase);
        }

        if (IsHost(host, "vimeo.com"))
            return path.Length > 1 && path.Skip(1).All(char.IsDigit);

        if (IsHost(host, "dailymotion.com") || host == "dai.ly")
            return path.Contains("/video/", StringComparison.OrdinalIgnoreCase) || host == "dai.ly";

        if (IsHost(host, "twitch.tv"))
            return path.Contains("/videos/", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("/clip/", StringComparison.OrdinalIgnoreCase);

        if (IsHost(host, "twitter.com") || host == "x.com")
            return path.Contains("/status/", StringComparison.OrdinalIgnoreCase);

        if (IsHost(host, "facebook.com") || host == "fb.watch")
            return path.Contains("/videos/", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("/watch", StringComparison.OrdinalIgnoreCase) ||
                   host == "fb.watch";

        if (IsHost(host, "instagram.com"))
            return path.StartsWith("/p/", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("/reel/", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("/tv/", StringComparison.OrdinalIgnoreCase);

        if (IsHost(host, "tiktok.com"))
            return path.Contains("/video/", StringComparison.OrdinalIgnoreCase);

        if (IsHost(host, "bilibili.com"))
            return path.StartsWith("/video/", StringComparison.OrdinalIgnoreCase);

        if (IsHost(host, "reddit.com") || host == "redd.it")
            return path.Contains("/comments/", StringComparison.OrdinalIgnoreCase) || host == "redd.it";

        // SoundCloud tracks, Streamable clips, Rumble and Odysee pages do not
        // share a stable path pattern; any content path is worth probing.
        return KnownHosts.Any(known => host == known || host.EndsWith("." + known, StringComparison.Ordinal)) &&
               path.Length > 1;
    }

    private static bool IsHost(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    private static bool TryParse(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed))
            return false;

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            return false;

        uri = parsed;
        return true;
    }
}
