using System.IO;
using System.Windows;
using Adm.App.Services;
using Adm.App.ViewModels;
using Adm.App.Views;
using Adm.Core;
using Adm.Core.Ipc;
using Adm.Core.Licensing;
using Adm.Core.Media;
using Adm.Core.Settings;
using Adm.Core.Storage;
using Adm.Core.Util;

namespace Adm.App;

public partial class App : Application
{
    private const string MutexName = @"Local\ADM.DownloadManager.SingleInstance";

    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;
    private DownloadManager? _manager;
    private MainViewModel? _viewModel;
    private SettingsStore? _settingsStore;
    private PipeServer? _pipeServer;
    private LicenseService? _license;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
        _ownsMutex = createdNew;

        if (!createdNew)
        {
            ForwardToRunningInstance(e.Args);
            Shutdown();
            return;
        }

        try
        {
            var settingsStore = new SettingsStore();
            var settings = settingsStore.Load();
            _settingsStore = settingsStore;

            // The diagnostics log lives next to the settings file.
            string logPath = Path.Combine(
                Path.GetDirectoryName(settingsStore.FilePath) ?? AppContext.BaseDirectory,
                "adm.log");
            DiagnosticsLog.Configure(logPath, settings.WriteLogFile);
            DiagnosticsLog.Info("ADM starting.");

            // The licence is checked before anything else is shown.
            _license = new LicenseService();
            if (!await EnsureLicensedAsync())
            {
                DiagnosticsLog.Info("ADM stopped because no valid licence was available.");
                Shutdown();
                return;
            }

            _manager = new DownloadManager(settings, new DownloadStore());
            _manager.Load();

            // The video engine uses the same network settings as the HTTP engine.
            _manager.VideoService.ProxyUrl = settings.ProxyUrl;
            _manager.VideoService.CookiesFromBrowser = settings.PassCookiesToYtDlp
                ? settings.CookiesFromBrowser
                : null;

            ThemeManager.Apply(settings.DarkTheme);

            _viewModel = new MainViewModel(_manager, settingsStore);
            var window = new MainWindow { DataContext = _viewModel };
            MainWindow = window;
            window.Show();

            _pipeServer = new PipeServer(HandlePipeMessage);
            _pipeServer.Start();

            _ = AutoUpdateVideoEngineAsync();
            _ = CheckForAppUpdateAsync();

            foreach (var argument in e.Args.Where(IsHttpUrl))
            {
                if (VideoUrlDetector.IsDirectMediaFile(argument))
                {
                    _viewModel.AddFromBrowser(argument, null, null, null);
                }
                else if (VideoUrlDetector.IsKnownVideoSite(argument))
                {
                    // Let yt-dlp report what is wrong with the page instead of
                    // downloading the site's HTML.
                    string videoUrl = argument;
                    _ = Dispatcher.BeginInvoke(new Action(() => window.OpenVideoDownload(videoUrl)));
                }
                else
                {
                    _viewModel.AddFromBrowser(argument, null, null, null);
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("ADM could not start.\n\n" + ex.Message, "ADM",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    /// <summary>
    /// Tells the user when a newer ADM release is published. The download is
    /// left to the browser, so the app never replaces itself.
    /// </summary>
    private async Task CheckForAppUpdateAsync()
    {
        try
        {
            var service = new AppUpdateService();
            var update = await service.CheckAsync();
            if (update is null)
                return;

            var answer = MessageBox.Show(
                $"ADM {update.Version} is available.\n\n{update.Notes}\n\nOpen the download page?",
                "ADM update",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (answer == MessageBoxResult.Yes)
                AppUpdateService.OpenDownload(update);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Info("The app update check failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Makes sure a valid licence is available. When there is none, the
    /// activation window is shown and the user can enter a key. The app only
    /// starts once a licence was accepted.
    /// </summary>
    private async Task<bool> EnsureLicensedAsync()
    {
        if (_license is null)
            return false;

        while (true)
        {
            var status = await _license.CheckAsync().ConfigureAwait(false);

            if (status.CanRun)
            {
                DiagnosticsLog.Info("Licence state: " + status.SummaryText);
                return true;
            }

            DiagnosticsLog.Info("Licence state: " + status.SummaryText + " - " + status.Message);

            // The activation window is the only thing the user can act on.
            var window = new LicenseWindow(_license);
            window.ShowDialog();

            if (window.DialogResult == true)
                return true;

            // The window was closed without a licence. The app may only
            // continue when the user chooses to quit.
            var answer = MessageBox.Show(
                "ADM needs a valid licence to run.\n\nClose ADM now?",
                "ADM",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer == MessageBoxResult.Yes)
            {
                DiagnosticsLog.Info("ADM stopped because no valid licence was available.");
                return false;
            }

            // Stay in the loop: the activation window comes up again.
        }
    }

    /// <summary>
    /// Brings the bundled yt-dlp up to date in the background, because site
    /// extractors break regularly and the video feature depends on them.
    /// </summary>
    private async Task AutoUpdateVideoEngineAsync()
    {
        if (_manager is null || !_manager.Settings.AutoUpdateYtDlp)
            return;

        try
        {
            var (success, message) = await _manager.VideoService.UpdateAsync();
            if (success)
                DiagnosticsLog.Info("The video engine was updated: " + message);
            else
                DiagnosticsLog.Warn("The video engine was not updated: " + message);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Warn("The video engine update failed.", ex);
        }
    }

    private PipeReply HandlePipeMessage(PipeMessage message)
    {
        try
        {
            return Dispatcher.Invoke(() =>
            {
                if (_manager is null || _viewModel is null)
                    return new PipeReply { Ok = false, Message = "ADM is not ready." };

                switch ((message.Action ?? string.Empty).ToUpperInvariant())
                {
                    case "ADD_DOWNLOAD":
                    case "START_DOWNLOAD":
                        if (string.IsNullOrWhiteSpace(message.Url))
                            return new PipeReply { Ok = false, Message = "No URL was supplied." };

                        var item = _viewModel.AddFromBrowser(message.Url, message.FileName,
                            message.Referrer, message.UserAgent, message.Cookie);

                        return item is null
                            ? new PipeReply { Ok = false, Message = _viewModel.StatusMessage }
                            : new PipeReply
                            {
                                Ok = true,
                                Message = "Queued",
                                DownloadId = item.Id.ToString(),
                                ActiveCount = CountActive()
                            };

                    case "ADD_DOWNLOADS":
                        if (string.IsNullOrWhiteSpace(message.Urls))
                            return new PipeReply { Ok = false, Message = "No URLs were supplied." };

                        int added = 0;
                        foreach (string url in message.Urls.Split('\n', StringSplitOptions.RemoveEmptyEntries |
                                                                 StringSplitOptions.TrimEntries))
                        {
                            if (_viewModel.AddFromBrowser(url, null, message.Referrer, message.UserAgent,
                                    message.Cookie) is not null)
                            {
                                added++;
                            }
                        }

                        return new PipeReply
                        {
                            Ok = added > 0,
                            Message = added == 1 ? "1 download queued." : $"{added} downloads queued.",
                            ActiveCount = CountActive()
                        };

                    case "OPEN_VIDEO":
                        if (string.IsNullOrWhiteSpace(message.Url))
                            return new PipeReply { Ok = false, Message = "No video URL was supplied." };

                        string videoUrl = message.Url!;

                        // A direct media file is not an extractor page; the
                        // segmented HTTP engine downloads it faster.
                        if (VideoUrlDetector.IsDirectMediaFile(videoUrl))
                        {
                            var mediaItem = _viewModel.AddFromBrowser(videoUrl, null,
                                message.Referrer, message.UserAgent);

                            return mediaItem is null
                                ? new PipeReply { Ok = false, Message = _viewModel.StatusMessage }
                                : new PipeReply
                                {
                                    Ok = true,
                                    Message = "Queued",
                                    DownloadId = mediaItem.Id.ToString(),
                                    ActiveCount = CountActive()
                                };
                        }

                        ShowMainWindow();
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (MainWindow is Adm.App.MainWindow window)
                                window.OpenVideoDownload(videoUrl);
                        }));
                        return new PipeReply { Ok = true, Message = "Opened", ActiveCount = CountActive() };

                    case "SHOW":
                        ShowMainWindow();
                        return new PipeReply { Ok = true, ActiveCount = CountActive() };

                    case "GET_INTERCEPT":
                        return new PipeReply
                        {
                            Ok = true,
                            Message = _manager.Settings.InterceptBrowserDownloads ? "1" : "0"
                        };

                    case "SET_INTERCEPT":
                        _manager.Settings.InterceptBrowserDownloads = message.Intercept != false;
                        _settingsStore?.Save(_manager.Settings);
                        return new PipeReply { Ok = true };

                    case "PING":
                        return new PipeReply { Ok = true, ActiveCount = CountActive() };

                    default:
                        return new PipeReply { Ok = false, Message = "Unknown action." };
                }
            });
        }
        catch (Exception ex)
        {
            return new PipeReply { Ok = false, Message = ex.Message };
        }
    }

    private int CountActive() => _manager?.Items.Count(item => item.IsActive) ?? 0;

    private void ShowMainWindow()
    {
        if (MainWindow is Adm.App.MainWindow window)
        {
            // The window may be hidden in the notification area.
            window.RestoreFromTray();
            return;
        }

        if (MainWindow is null)
            return;

        if (MainWindow.WindowState == WindowState.Minimized)
            MainWindow.WindowState = WindowState.Normal;

        MainWindow.Show();
        MainWindow.Activate();
        MainWindow.Topmost = true;
        MainWindow.Topmost = false;
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Sends this launch's URL to the already running instance, then exits.</summary>
    private static void ForwardToRunningInstance(string[] args)
    {
        try
        {
            var url = args.FirstOrDefault(IsHttpUrl);
            var message = url is null
                ? new PipeMessage { Action = "SHOW" }
                : VideoUrlDetector.IsDirectMediaFile(url)
                    ? new PipeMessage { Action = "ADD_DOWNLOAD", Url = url }
                    : VideoUrlDetector.IsKnownVideoSite(url)
                        ? new PipeMessage { Action = "OPEN_VIDEO", Url = url }
                        : new PipeMessage { Action = "ADD_DOWNLOAD", Url = url };

            Task.Run(() => PipeClient.SendAsync(message, TimeSpan.FromSeconds(3))).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // The first instance may still be starting; the user can paste the link instead.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pipeServer?.Dispose();
        _viewModel?.Dispose();
        _manager?.Dispose();

        if (_singleInstanceMutex is not null)
        {
            if (_ownsMutex)
            {
                try
                {
                    _singleInstanceMutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Not owned by this thread; safe to ignore during shutdown.
                }
            }

            _singleInstanceMutex.Dispose();
        }

        base.OnExit(e);
    }
}
