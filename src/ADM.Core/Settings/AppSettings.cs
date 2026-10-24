using Adm.Core.Models;

namespace Adm.Core.Settings;

/// <summary>User preferences. Persisted as JSON in %APPDATA%\ADM\settings.json.</summary>
public sealed class AppSettings
{
    public string DownloadFolder { get; set; } = DefaultDownloadFolder();

    /// <summary>Ask before starting a download unless that file type has saved preferences.</summary>
    public bool AskBeforeDownload { get; set; } = true;

    /// <summary>Saved destination and transfer preferences keyed by file extension.</summary>
    public Dictionary<string, DownloadTypePreference> DownloadTypePreferences { get; set; } = new();

    /// <summary>Number of parallel connections used for a segmented download.</summary>
    public int MaxConnections { get; set; } = 8;

    /// <summary>How many downloads may run at the same time.</summary>
    public int MaxConcurrentDownloads { get; set; } = 3;

    /// <summary>Global speed cap in bytes/second. 0 means unlimited.</summary>
    public long SpeedLimitBytesPerSecond { get; set; }

    public bool AutoResumeOnStartup { get; set; }

    public bool StartWithWindows { get; set; }

    /// <summary>When Windows starts ADM, run in the system tray instead of opening the main window.</summary>
    public bool StartInTray { get; set; } = true;

    /// <summary>When enabled, the browser extension hands downloads to ADM automatically.</summary>
    public bool InterceptBrowserDownloads { get; set; } = true;

    /// <summary>Whether the first-run browser extension setup guide has been shown.</summary>
    public bool ExtensionSetupShown { get; set; }

    /// <summary>Show a confirmation before deleting a downloaded file from disk.</summary>
    public bool ConfirmBeforeDelete { get; set; } = true;

    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";

    // ---------------------------------------------------------------- network

    /// <summary>Proxy URL applied to every HTTP request, for example http://host:port.</summary>
    public string? ProxyUrl { get; set; }

    /// <summary>User name for a proxy that requires authentication.</summary>
    public string? ProxyUsername { get; set; }

    /// <summary>Password for a proxy that requires authentication.</summary>
    public string? ProxyPassword { get; set; }

    /// <summary>HTTP basic authentication user name sent with every request.</summary>
    public string? BasicAuthUsername { get; set; }

    /// <summary>HTTP basic authentication password sent with every request.</summary>
    public string? BasicAuthPassword { get; set; }

    /// <summary>Cookie header sent with every request, for example "session=abc; token=def".</summary>
    public string? CookieHeader { get; set; }

    /// <summary>Browser whose cookies yt-dlp may read for age-restricted videos.</summary>
    public string? CookiesFromBrowser { get; set; }

    /// <summary>Forward the browser cookies to yt-dlp as well.</summary>
    public bool PassCookiesToYtDlp { get; set; } = true;

    // --------------------------------------------------------------- behaviour

    /// <summary>Show a notification when a download finishes or fails.</summary>
    public bool NotifyOnComplete { get; set; } = true;

    /// <summary>Watch the clipboard and offer to download copied links.</summary>
    public bool MonitorClipboard { get; set; }

    /// <summary>Refuse a download when the target drive has no room for it.</summary>
    public bool CheckDiskSpace { get; set; } = true;

    /// <summary>What to do when the same URL is queued twice.</summary>
    public DuplicatePolicy DuplicatePolicy { get; set; } = DuplicatePolicy.Allow;

    /// <summary>What to do when the target file already exists.</summary>
    public FileExistsPolicy FileExistsPolicy { get; set; } = FileExistsPolicy.Rename;

    /// <summary>How often a failed download is retried.</summary>
    public RetryPolicy RetryPolicy { get; set; } = RetryPolicy.Limited;

    /// <summary>How many times a limited retry policy retries a transfer.</summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>Open the finished file automatically.</summary>
    public bool OpenWhenFinished { get; set; }

    /// <summary>Shut ADM down when every download has finished.</summary>
    public bool ExitWhenFinished { get; set; }

    /// <summary>Write a diagnostics log next to the settings file.</summary>
    public bool WriteLogFile { get; set; } = true;

    // ------------------------------------------------------------------ video

    /// <summary>Preferred audio bitrate for MP3 output, in kbps.</summary>
    public int AudioBitrateKbps { get; set; } = 192;

    /// <summary>Download subtitles next to the video when the site offers them.</summary>
    public bool DownloadSubtitles { get; set; }

    /// <summary>Comma separated subtitle languages, for example "en,de".</summary>
    public string SubtitleLanguages { get; set; } = "en";

    /// <summary>Allow downloading a whole playlist instead of a single video.</summary>
    public bool AllowPlaylists { get; set; }

    /// <summary>Check the bundled yt-dlp for a newer version on startup.</summary>
    public bool AutoUpdateYtDlp { get; set; } = true;

    // -------------------------------------------------------------- app update

    /// <summary>Look for a newer ADM release in the background.</summary>
    public bool AutoUpdateApp { get; set; } = true;

    /// <summary>How often ADM looks for a newer release, in hours.</summary>
    public int AppUpdateIntervalHours { get; set; } = 1;

    /// <summary>URL of the app release feed.</summary>
    public string? AppUpdateFeedUrl { get; set; }

    /// <summary>True when a newer release may be installed without asking.</summary>
    public bool AutoInstallAppUpdates { get; set; }

    // -------------------------------------------------------------- scheduler

    /// <summary>Start queued downloads at this local time. Null means "as soon as possible".</summary>
    public TimeSpan? ScheduledStartTime { get; set; }

    /// <summary>Stop active downloads at this local time. Null means "never".</summary>
    public TimeSpan? ScheduledStopTime { get; set; }

    public static string DefaultDownloadFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ADM");

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public void Normalize()
    {
        DownloadTypePreferences ??= new Dictionary<string, DownloadTypePreference>();
        DownloadTypePreferences = DownloadTypePreferences
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
            .GroupBy(pair => pair.Key.Trim().ToLowerInvariant(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Value,
                StringComparer.OrdinalIgnoreCase);
        foreach (DownloadTypePreference preference in DownloadTypePreferences.Values)
        {
            preference.Connections = Math.Clamp(preference.Connections, 1, 32);
            preference.SpeedLimitBytesPerSecond = Math.Max(0, preference.SpeedLimitBytesPerSecond);
        }

        if (string.IsNullOrWhiteSpace(DownloadFolder))
            DownloadFolder = DefaultDownloadFolder();

        MaxConnections = Math.Clamp(MaxConnections, 1, 32);
        MaxConcurrentDownloads = Math.Clamp(MaxConcurrentDownloads, 1, 16);
        SpeedLimitBytesPerSecond = Math.Max(0, SpeedLimitBytesPerSecond);
MaxRetries = Math.Clamp(MaxRetries, 0, 50);
        AudioBitrateKbps = Math.Clamp(AudioBitrateKbps, 32, 240);
        AppUpdateIntervalHours = Math.Clamp(AppUpdateIntervalHours, 1, 720);

        if (string.IsNullOrWhiteSpace(UserAgent))
            UserAgent = new AppSettings().UserAgent;

        if (string.IsNullOrWhiteSpace(SubtitleLanguages))
            SubtitleLanguages = "en";
    }
}

/// <summary>Reusable download settings that can be remembered for one extension.</summary>
public sealed class DownloadTypePreference
{
    public string Folder { get; set; } = string.Empty;
    public int Connections { get; set; } = 8;
    public long SpeedLimitBytesPerSecond { get; set; }
}
