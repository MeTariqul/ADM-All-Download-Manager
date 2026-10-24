using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Adm.Core.Util;

namespace Adm.App.Services;

public sealed class AppAutoUpdateService
{
    private const string DefaultFeedUrl = "https://github.com/MeTariqul/ADM-All-Download-Manager/releases/latest.json";

    private readonly HttpClient _client;
    private readonly string _installDir;

    public AppAutoUpdateService(HttpClient? client = null, string? installDir = null)
    {
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _installDir = installDir ?? AppContext.BaseDirectory;
    }

    public string FeedUrl { get; set; } = DefaultFeedUrl;

    public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(FeedUrl))
            return null;

        try
        {
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("ADM/" + AppUpdateService.CurrentVersion);
            string json = await _client.GetStringAsync(FeedUrl, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            string latest = root.TryGetProperty("version", out var version) ? version.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(latest))
                return null;

            string url = root.TryGetProperty("installer", out var installer)
                ? installer.GetString() ?? string.Empty
                : (root.TryGetProperty("url", out var link) ? link.GetString() ?? string.Empty : string.Empty);

            string notes = root.TryGetProperty("notes", out var note) ? note.GetString() ?? string.Empty : string.Empty;

            if (!AppUpdateService.IsNewer(latest, AppUpdateService.CurrentVersion))
                return null;

            return new UpdateInfo { Version = latest, DownloadUrl = url, Notes = notes };
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Info("The app update check failed: " + ex.Message);
            return null;
        }
    }

    public async Task<bool> DownloadAndInstallAsync(UpdateInfo info, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(info?.DownloadUrl))
            return false;

        try
        {
            string temp = Path.Combine(Path.GetTempPath(), "ADM", "ADM-Setup-" + info.Version + ".exe");
            Directory.CreateDirectory(Path.GetDirectoryName(temp)!);

            using var response = await _client.GetStreamAsync(info.DownloadUrl, cancellationToken).ConfigureAwait(false);
            await using var fileStream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
            await response.CopyToAsync(fileStream, 81920, cancellationToken).ConfigureAwait(false);

            DiagnosticsLog.Info("Downloaded installer for ADM " + info.Version + " to " + temp);

            var start = new ProcessStartInfo(temp)
            {
                UseShellExecute = true,
                Arguments = "/SILENT /NORESTART",
                Verb = "runas"
            };

            Process.Start(start);
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Warn("The automatic update failed: " + ex.Message, ex);
            return false;
        }
    }
}
