using System.IO;
using System.Diagnostics;
using System.Windows;
using Adm.Core.Licensing;
using Adm.Core.Media;
using Adm.Core.Models;
using Adm.Core.Settings;
using Microsoft.Win32;

namespace Adm.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private bool _clearTypePreferencesRequested;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        FolderBox.Text = settings.DownloadFolder;
        ConnectionsBox.Text = settings.MaxConnections.ToString();
        ConcurrentBox.Text = settings.MaxConcurrentDownloads.ToString();
        SpeedLimitBox.Text = (settings.SpeedLimitBytesPerSecond / 1024).ToString();
        AskBeforeDownloadCheck.IsChecked = settings.AskBeforeDownload;
        RefreshSavedTypesText();
        AutoResumeCheck.IsChecked = settings.AutoResumeOnStartup;
        StartWithWindowsCheck.IsChecked = settings.StartWithWindows;
        ConfirmDeleteCheck.IsChecked = settings.ConfirmBeforeDelete;
        NotifyCheck.IsChecked = settings.NotifyOnComplete;
        ClipboardCheck.IsChecked = settings.MonitorClipboard;
        DiskSpaceCheck.IsChecked = settings.CheckDiskSpace;
        OpenWhenFinishedCheck.IsChecked = settings.OpenWhenFinished;
        ExitWhenFinishedCheck.IsChecked = settings.ExitWhenFinished;
        LogFileCheck.IsChecked = settings.WriteLogFile;
        InterceptCheck.IsChecked = settings.InterceptBrowserDownloads;
        SubtitlesCheck.IsChecked = settings.DownloadSubtitles;
        AutoUpdateCheck.IsChecked = settings.AutoUpdateYtDlp;
        AllowPlaylistsCheck.IsChecked = settings.AllowPlaylists;

        ProxyBox.Text = settings.ProxyUrl ?? string.Empty;
        ProxyUserBox.Text = settings.ProxyUsername ?? string.Empty;
        ProxyPasswordBox.Text = settings.ProxyPassword ?? string.Empty;
        AuthUserBox.Text = settings.BasicAuthUsername ?? string.Empty;
        AuthPasswordBox.Text = settings.BasicAuthPassword ?? string.Empty;
        CookieBox.Text = settings.CookieHeader ?? string.Empty;
        CookiesBrowserBox.Text = settings.CookiesFromBrowser ?? string.Empty;
        AudioBitrateBox.Text = settings.AudioBitrateKbps.ToString();
        SubtitleLanguagesBox.Text = settings.SubtitleLanguages;
        MaxRetriesBox.Text = settings.MaxRetries.ToString();

        DuplicatePolicyBox.ItemsSource = new[] { "Allow a second copy", "Skip the duplicate", "Resume the existing one" };
        DuplicatePolicyBox.SelectedIndex = (int)settings.DuplicatePolicy;

        FileExistsPolicyBox.ItemsSource = new[] { "Rename the new file", "Overwrite the old file", "Keep the old file" };
        FileExistsPolicyBox.SelectedIndex = (int)settings.FileExistsPolicy;

        RetryPolicyBox.ItemsSource = new[] { "Do not retry", "Retry a few times", "Keep retrying" };
        RetryPolicyBox.SelectedIndex = (int)settings.RetryPolicy;

        StartTimeCheck.IsChecked = settings.ScheduledStartTime is not null;
        if (settings.ScheduledStartTime is { } start)
            StartTimeBox.Text = start.ToString(@"hh\:mm");
        else
            StartTimeBox.Text = "00:00";

        StopTimeCheck.IsChecked = settings.ScheduledStopTime is not null;
        if (settings.ScheduledStopTime is { } stop)
            StopTimeBox.Text = stop.ToString(@"hh\:mm");
        else
            StopTimeBox.Text = "00:00";

        _videoService = new YtDlpService();
        VideoEngineText.Text = DescribeVideoEngine(_videoService);
        _ = RefreshVersionAsync();

        _license = new LicenseService();
        _ = RefreshLicenseAsync();
    }

    private readonly YtDlpService _videoService;
    private readonly LicenseService _license;
    private bool _updating;

    /// <summary>Shows the current licence state.</summary>
    private async Task RefreshLicenseAsync()
    {
        var status = await _license.CheckAsync();
        string expiry = status.ExpiresAt is { } date
            ? $" Valid until {date.LocalDateTime:d MMM yyyy}."
            : string.Empty;

        LicenseText.Text = $"{status.SummaryText}.{expiry} {status.Message}  Device {DeviceIdentity.ShortId}.";

        if (!status.CanRun)
        {
            LicenseTrialStatus trial = _license.GetTrialStatus();
            LicenseText.Text += trial.IsActive
                ? $" Trial active until {trial.EndsAt?.LocalDateTime:d MMM yyyy} ({Math.Ceiling(trial.Remaining.TotalDays):F0} day(s) left)."
                : " The seven-day trial has ended or is unavailable.";
        }

        await CheckServerKeyAsync();
    }

    /// <summary>
    /// Compares the key that ships with the app with the one the server uses.
    /// A mismatch means the server rotated its key and this build is too old.
    /// </summary>
    private async Task CheckServerKeyAsync()
    {
        try
        {
            var client = new LicenseClient(LicenseConfiguration.EffectiveAppKey);
            var (ok, publicKey, _, _) = await client.GetPublicKeyAsync();

            if (!ok)
            {
                LicenseText.Text += "  The licence server could not be reached.";
                return;
            }

            string shipped = LicenseConfiguration.PublicKeyPem.Replace("\r", string.Empty).Replace("\n", string.Empty);
            string server = publicKey.Replace("\r", string.Empty).Replace("\n", string.Empty);

            if (!string.Equals(shipped, server, StringComparison.Ordinal))
            {
                LicenseText.Text += "  The licence server uses a different key; this build needs an update.";
            }
        }
        catch (Exception)
        {
            // The check is a convenience; a failure must not break the settings.
        }
    }

    private async void OnManageLicense(object sender, RoutedEventArgs e)
    {
        var window = new LicenseWindow(_license) { Owner = this };
        window.ShowDialog();
        await RefreshLicenseAsync();
    }

    private void OnCopyDeviceId(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_license.DeviceId);
            LicenseText.Text = "The device id was copied to the clipboard.";
        }
        catch (Exception ex)
        {
            LicenseText.Text = ex.Message;
        }
    }

    private void OnOpenGitHub(object sender, RoutedEventArgs e) => OpenCreatorLink("https://github.com/MeTariqul");

    private void OnOpenLinkedIn(object sender, RoutedEventArgs e) => OpenCreatorLink("https://www.linkedin.com/in/metariqul/");

    private void OpenCreatorLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not open the link.\n\n" + ex.Message, "ADM",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>Reports whether the external video tools were found.</summary>
    private static string DescribeVideoEngine(YtDlpService service)
    {
        if (!service.IsAvailable)
        {
            return "yt-dlp was not found, so video pages cannot be downloaded. " +
                   "Run tools\\Get-VideoTools.ps1 or reinstall ADM.";
        }

        string engine = service.HasFfmpeg
            ? "yt-dlp and ffmpeg were found. Video, merged audio and MP3 downloads are available."
            : "yt-dlp was found, but ffmpeg is missing. Merged video+audio and MP3 output are unavailable.";

        return engine + "  (" + service.YtDlpPath + ")";
    }

    private async Task RefreshVersionAsync()
    {
        string version = await _videoService.GetVersionAsync();
        VersionText.Text = string.IsNullOrWhiteSpace(version)
            ? "The version of the bundled yt-dlp is unknown."
            : $"The bundled yt-dlp is version {version}.";
    }

    private async void OnUpdateYtDlp(object sender, RoutedEventArgs e)
    {
        if (_updating)
            return;

        _updating = true;
        UpdateYtDlpButton.IsEnabled = false;
        UpdateYtDlpButton.Content = "Updating...";

        try
        {
            var (success, message) = await _videoService.UpdateAsync();
            MessageBox.Show(this, message, "Video engine",
                MessageBoxButton.OK, success ? MessageBoxImage.Information : MessageBoxImage.Warning);

            await RefreshVersionAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Video engine", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _updating = false;
            UpdateYtDlpButton.IsEnabled = true;
            UpdateYtDlpButton.Content = "Update yt-dlp now";
        }
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the default download folder"
        };

        if (Directory.Exists(FolderBox.Text))
            dialog.InitialDirectory = FolderBox.Text;

        if (dialog.ShowDialog(this) == true)
            FolderBox.Text = dialog.FolderName;
    }

    private void OnClearTypePreferences(object sender, RoutedEventArgs e)
    {
        _clearTypePreferencesRequested = true;
        RefreshSavedTypesText();
    }

    private void RefreshSavedTypesText()
    {
        SavedTypesText.Text = _clearTypePreferencesRequested || _settings.DownloadTypePreferences.Count == 0
            ? "No file type preferences are saved yet. Use the review prompt to remember a destination and transfer setup."
            : "Saved types: " + string.Join(", ", _settings.DownloadTypePreferences.Keys.OrderBy(key => key));
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(ConnectionsBox.Text.Trim(), out int connections) || connections is < 1 or > 32)
        {
            MessageBox.Show(this, "Connections per download must be a number between 1 and 32.",
                "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(ConcurrentBox.Text.Trim(), out int concurrent) || concurrent is < 1 or > 16)
        {
            MessageBox.Show(this, "Simultaneous downloads must be a number between 1 and 16.",
                "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!long.TryParse(SpeedLimitBox.Text.Trim(), out long limitKb) || limitKb < 0)
        {
            MessageBox.Show(this, "Speed limit must be 0 (unlimited) or a positive number of KB/s.",
                "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(MaxRetriesBox.Text.Trim(), out int maxRetries) || maxRetries is < 0 or > 50)
        {
            MessageBox.Show(this, "Retries must be a number between 0 and 50.",
                "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(AudioBitrateBox.Text.Trim(), out int bitrate) || bitrate is < 32 or > 320)
        {
            MessageBox.Show(this, "The audio bitrate must be a number between 32 and 320 kbps.",
                "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        TimeSpan? startTime = StartTimeCheck.IsChecked == true ? ParseTime(StartTimeBox.Text) : null;
        TimeSpan? stopTime = StopTimeCheck.IsChecked == true ? ParseTime(StopTimeBox.Text) : null;

        string folder = FolderBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder))
            folder = AppSettings.DefaultDownloadFolder();

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"That folder cannot be used:\n\n{ex.Message}",
                "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.DownloadFolder = folder;
        if (_clearTypePreferencesRequested)
            _settings.DownloadTypePreferences.Clear();
        _settings.MaxConnections = connections;
        _settings.MaxConcurrentDownloads = concurrent;
        _settings.SpeedLimitBytesPerSecond = Math.Min(limitKb * 1024, 10L * 1024 * 1024 * 1024);
        _settings.AskBeforeDownload = AskBeforeDownloadCheck.IsChecked == true;
        _settings.AutoResumeOnStartup = AutoResumeCheck.IsChecked == true;
        _settings.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
        _settings.ConfirmBeforeDelete = ConfirmDeleteCheck.IsChecked == true;
        _settings.NotifyOnComplete = NotifyCheck.IsChecked == true;
        _settings.MonitorClipboard = ClipboardCheck.IsChecked == true;
        _settings.CheckDiskSpace = DiskSpaceCheck.IsChecked == true;
        _settings.OpenWhenFinished = OpenWhenFinishedCheck.IsChecked == true;
        _settings.ExitWhenFinished = ExitWhenFinishedCheck.IsChecked == true;
        _settings.WriteLogFile = LogFileCheck.IsChecked == true;
        _settings.InterceptBrowserDownloads = InterceptCheck.IsChecked == true;
        _settings.DownloadSubtitles = SubtitlesCheck.IsChecked == true;
        _settings.AutoUpdateYtDlp = AutoUpdateCheck.IsChecked == true;
        _settings.AllowPlaylists = AllowPlaylistsCheck.IsChecked == true;

        _settings.ProxyUrl = NullIfEmpty(ProxyBox.Text);
        _settings.ProxyUsername = NullIfEmpty(ProxyUserBox.Text);
        _settings.ProxyPassword = NullIfEmpty(ProxyPasswordBox.Text);
        _settings.BasicAuthUsername = NullIfEmpty(AuthUserBox.Text);
        _settings.BasicAuthPassword = NullIfEmpty(AuthPasswordBox.Text);
        _settings.CookieHeader = NullIfEmpty(CookieBox.Text);
        _settings.CookiesFromBrowser = NullIfEmpty(CookiesBrowserBox.Text);
        _settings.SubtitleLanguages = string.IsNullOrWhiteSpace(SubtitleLanguagesBox.Text)
            ? "en"
            : SubtitleLanguagesBox.Text.Trim();
        _settings.AudioBitrateKbps = bitrate;
        _settings.MaxRetries = maxRetries;

        _settings.DuplicatePolicy = (DuplicatePolicy)Math.Clamp(DuplicatePolicyBox.SelectedIndex, 0, 2);
        _settings.FileExistsPolicy = (FileExistsPolicy)Math.Clamp(FileExistsPolicyBox.SelectedIndex, 0, 2);
        _settings.RetryPolicy = (RetryPolicy)Math.Clamp(RetryPolicyBox.SelectedIndex, 0, 2);

        _settings.ScheduledStartTime = startTime;
        _settings.ScheduledStopTime = stopTime;

        DialogResult = true;
    }

    /// <summary>Parses a "hh:mm" time, returning null when the text is not valid.</summary>
    private static TimeSpan? ParseTime(string text)
    {
        if (!TimeSpan.TryParse(text.Trim(), out var value))
            return null;

        if (value < TimeSpan.Zero || value >= TimeSpan.FromDays(1))
            return null;

        return value;
    }

    private static string? NullIfEmpty(string text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
