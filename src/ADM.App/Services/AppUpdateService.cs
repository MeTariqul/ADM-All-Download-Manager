using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Adm.Core.Util;

namespace Adm.App.Services;

/// <summary>
/// Checks whether a newer ADM release is published and offers to download it.
/// The feed is a small JSON document with a version and a download link; the
/// app never installs anything by itself.
/// </summary>
public sealed class AppUpdateService
{
    private const string DefaultFeedUrl = "https://adm.example.invalid/latest.json";

    private readonly HttpClient _client;

    public AppUpdateService(HttpClient? client = null)
    {
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>URL of the release feed.</summary>
    public string FeedUrl { get; set; } = DefaultFeedUrl;

    /// <summary>The version of the running app, for example "1.0.0".</summary>
    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Asks the feed for the newest release. Returns null when the app is
    /// already up to date or the feed cannot be reached.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(FeedUrl))
            return null;

        try
        {
            string json = await _client.GetStringAsync(FeedUrl, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);

            string latest = document.RootElement.TryGetProperty("version", out var version)
                ? version.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(latest))
                return null;

            string url = document.RootElement.TryGetProperty("url", out var link)
                ? link.GetString() ?? string.Empty
                : string.Empty;

            string notes = document.RootElement.TryGetProperty("notes", out var note)
                ? note.GetString() ?? string.Empty
                : string.Empty;

            if (!IsNewer(latest, CurrentVersion))
                return null;

            return new UpdateInfo
            {
                Version = latest,
                DownloadUrl = url,
                Notes = notes
            };
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Info("The update check failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>Opens the download page in the browser.</summary>
    public static void OpenDownload(UpdateInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.DownloadUrl))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(info.DownloadUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Warn("Could not open the update page.", ex);
        }
    }

    /// <summary>Compares two dotted version numbers.</summary>
    internal static bool IsNewer(string candidate, string current)
    {
        if (!Version.TryParse(candidate, out var left))
            return false;

        if (!Version.TryParse(current, out var right))
            return false;

        return left > right;
    }
}

/// <summary>Details of an available update.</summary>
public sealed class UpdateInfo
{
    public string Version { get; init; } = string.Empty;

    public string DownloadUrl { get; init; } = string.Empty;

    public string Notes { get; init; } = string.Empty;

    public string SummaryText =>
        string.IsNullOrWhiteSpace(Notes) ? $"Version {Version} is available." : $"Version {Version}: {Notes}";
}
