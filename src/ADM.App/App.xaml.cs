using System.IO;
using System.Windows;
using System.Windows.Threading;
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
    private DispatcherTimer? _trialTimer;
    private DispatcherTimer? _updateTimer;
    private bool _runningOnTrial;

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

            _viewModel = new MainViewModel(_manager, settingsStore);
            var window = new MainWindow { DataContext = _viewModel };
            MainWindow = window;

            // A Windows-started ADM (--minimized) runs in the notification
            // area without opening the main window. A launch with a URL always
            // shows the window, so the user sees what happened to the link.
            bool startInTray = IsMinimizedLaunch(e.Args) && !e.Args.Any(IsHttpUrl);
            if (startInTray)
            {
                DiagnosticsLog.Info("ADM started in the notification area.");
                window.StartHidden();
            }
            else
            {
                window.Show();
            }

            // Older installs have a Run entry without the tray flag. Rewrite
            // it (when present) so the Windows start honours the tray setting.
            if (settings.StartWithWindows)
                StartupRegistration.Apply(enabled: true, settings.StartInTray);
            else
                StartupRegistration.RefreshExisting(settings.StartInTray);

            if (_runningOnTrial)
                StartTrialMonitor(window);

            _pipeServer = new PipeServer(HandlePipeMessage);
            _pipeServer.Start();

            if (!settings.ExtensionSetupShown)
            {
                var setupWindow = new ExtensionSetupWindow(ExtensionSetupWindow.ResolveExtensionDirectory())
                {
                    Owner = window
                };
                setupWindow.ShowDialog();
                settings.ExtensionSetupShown = true;
                try
                {
                    settingsStore.Save(settings);
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Info("Could not save the extension setup prompt state: " + ex.Message);
                }
            }

            _ = AutoUpdateVideoEngineAsync();
            _ = CheckForAppUpdateAsync();
            StartAppUpdateTimer(window);

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
    private async Task CheckForAppUpdateAsync(bool silent = false)
    {
        try
        {
            var settingsStore = new SettingsStore();
            var settings = settingsStore.Load();

            var service = new AppUpdateService();
            var update = await service.CheckAsync();
            if (update is null)
                return;

            if (!silent)
            {
                var answer = MessageBox.Show(
                    $"ADM {update.Version} is available.\n\n{update.Notes}\n\nOpen the download page?",
                    "ADM update",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (answer == MessageBoxResult.Yes)
                    AppUpdateService.OpenDownload(update);
                return;
            }

            if (!settings.AutoUpdateApp && !settings.AutoInstallAppUpdates)
                return;

            DiagnosticsLog.Info("A newer ADM release is available (" + update.Version + ").");

            if (settings.AutoInstallAppUpdates)
            {
                await AutoInstallAppUpdate(update);
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Info("The app update check failed: " + ex.Message);
        }
    }

    private async Task AutoInstallAppUpdate(UpdateInfo update)
    {
        try
        {
            var service = new AppAutoUpdateService();
            bool ok = await service.DownloadAndInstallAsync(update);
            if (ok)
                DiagnosticsLog.Info("Started ADM installer for " + update.Version + ".");
            else
                DiagnosticsLog.Info("Could not start the ADM installer for " + update.Version + ".");
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Warn("The automatic app update failed: " + ex.Message, ex);
        }
    }

    private void StartAppUpdateTimer(Window window)
    {
        _updateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromHours(1)
        };
        _updateTimer.Tick += async (_, _) =>
        {
            try { await CheckForAppUpdateAsync(silent: true); }
            catch { }
        };
        _updateTimer.Start();
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
            // Keep the WPF startup continuation on the dispatcher; the next
            // step may need to show the activation dialog.
            var status = await _license.CheckAsync();

            if (status.CanRun)
            {
                DiagnosticsLog.Info("Licence state: " + status.SummaryText);
                return true;
            }

            DiagnosticsLog.Info("Licence state: " + status.SummaryText + " - " + status.Message);

            LicenseTrialStatus trial = _license.GetTrialStatus();

            // The activation window is the only thing the user can act on.
            var window = new LicenseWindow(_license, trial);
            window.ShowDialog();

            if (window.DialogResult == true)
                return true;

            if (trial.IsActive && window.UserCancelled)
            {
                _runningOnTrial = true;
                DiagnosticsLog.Info($"ADM continued in trial mode; {trial.Remaining.TotalHours:F0} hours remain.");
                return true;
            }

            // The window was closed without a licence. The app may only
            // continue when the user chooses to quit.
            var answer = MessageBox.Show(
                "ADM needs a valid licence or an unexpired trial to run.\n\nClose ADM now?",
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

    private void StartTrialMonitor(Window owner)
    {
        _trialTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _trialTimer.Tick += (_, _) =>
        {
            if (_license?.GetTrialStatus().IsActive == true)
                return;

            _trialTimer.Stop();
            DiagnosticsLog.Info("ADM trial has ended while the app was running.");
            var activation = new LicenseWindow(_license!, LicenseTrialStatus.Expired) { Owner = owner };
            activation.ShowDialog();
            if (activation.DialogResult == true)
            {
                _runningOnTrial = false;
                return;
            }

            Shutdown();
        };
        _trialTimer.Start();
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

    /// <summary>True when the launch asked for the notification-area start.</summary>
    private static bool IsMinimizedLaunch(string[] args) =>
        args.Any(arg =>
            arg.Equals(StartupRegistration.MinimizedArgument, StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("/minimized", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("--tray", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("/tray", StringComparison.OrdinalIgnoreCase));

    /// <summary>Sends this launch's URL to the already running instance, then exits.</summary>
    private static void ForwardToRunningInstance(string[] args)
    {
        try
        {
            var url = args.FirstOrDefault(IsHttpUrl);

            // A background start must not pop the window of the running copy.
            var message = url is null
                ? IsMinimizedLaunch(args)
                    ? new PipeMessage { Action = "PING" }
                    : new PipeMessage { Action = "SHOW" }
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
        _trialTimer?.Stop();
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
