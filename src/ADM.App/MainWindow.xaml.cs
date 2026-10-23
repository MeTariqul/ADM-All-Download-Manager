using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Adm.App.Services;
using Adm.App.ViewModels;
using Adm.App.Views;

namespace Adm.App;

public partial class MainWindow : Window
{
    private bool _shutdownStarted;
    private bool _videoDialogOpen;
    private bool _exitRequested;
    private bool _trayNoticeShown;
    private TrayIconService? _tray;
    private NotificationService? _notifications;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>Opens the format picker for a video page (also used by the browser extension).</summary>
    public void OpenVideoDownload(string url)
    {
        if (_videoDialogOpen || DataContext is not MainViewModel viewModel)
            return;

        _videoDialogOpen = true;
        try
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;

            if (!IsVisible)
                Show();

            Activate();

            var dialog = new VideoDownloadWindow(url, viewModel.VideoService, viewModel.Settings)
            {
                Owner = this
            };

            bool? result = dialog.ShowDialog();

            if (result == true && dialog.SelectedInfo is { } info && dialog.SelectedFormat is { } format)
            {
                viewModel.AddVideo(info, format);
            }
            else if (result == true && dialog.SelectedPlaylist is { } playlist)
            {
                OpenPlaylist(playlist);
            }
            else
            {
                viewModel.StatusMessage = "Video download cancelled.";
            }
        }
        finally
        {
            _videoDialogOpen = false;
            AddUrlBox.Focus();
        }
    }

    /// <summary>Lets the user pick the videos of a playlist and queues them.</summary>
    private void OpenPlaylist(Adm.Core.Media.PlaylistInfo playlist)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        var dialog = new PlaylistWindow(playlist.Url, viewModel.VideoService, viewModel.Settings)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true && dialog.SelectedEntries.Count > 0)
            viewModel.AddPlaylistEntries(dialog.SelectedEntries);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            int darkTitleBar = 0;
            DwmSetWindowAttribute(handle, 20, ref darkTitleBar, Marshal.SizeOf<int>());
        }
        catch (Exception)
        {
            // Title bar styling is cosmetic; unsupported systems keep the default.
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AddUrlBox.Focus();

        if (DataContext is MainViewModel viewModel)
        {
            viewModel.VideoRequested += OnVideoRequested;
            viewModel.ClipboardLinkFound += OnClipboardLinkFound;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        _tray = new TrayIconService(this, ShowFromTray, PauseAllFromTray, ResumeAllFromTray, ExitFromTray,
            CheckForUpdatesFromTray);
        _notifications = new NotificationService(_tray);

        if (DataContext is MainViewModel model)
        {
            model.AttachNotifications(_notifications);
            model.ApplyClipboardMonitoring();
        }
    }

    /// <summary>Keeps the view model's selection in step with the list.</summary>
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        viewModel.Selection.Clear();
        foreach (var item in DownloadList.SelectedItems.OfType<Adm.Core.Models.DownloadItem>())
            viewModel.Selection.Add(item);
    }

    private void OnDownloadListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        var item = DownloadList.SelectedItem as Adm.Core.Models.DownloadItem;
        if (item is null)
            return;

        if (item.Status == Adm.Core.Models.DownloadStatus.Completed)
        {
            viewModel.OpenFileCommand.Execute(item);
        }
        else
        {
            viewModel.ShowOptionsCommand.Execute(item);
        }
    }

    private void OnDownloadListKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        var selected = viewModel.Selection.ToList();
        if (selected.Count == 0)
            return;

        if (e.Key == Key.Delete)
        {
            viewModel.RemoveCommand.Execute(selected);
            e.Handled = true;
        }
        else if (e.Key == Key.Space)
        {
            if (selected.Any(i => i.CanPause))
                viewModel.PauseCommand.Execute(selected);
            else if (selected.Any(i => i.CanResume))
                viewModel.ResumeCommand.Execute(selected);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            var first = selected.FirstOrDefault();
            if (first is not null)
            {
                if (first.Status == Adm.Core.Models.DownloadStatus.Completed)
                    viewModel.OpenFileCommand.Execute(first);
                else
                    viewModel.ShowOptionsCommand.Execute(first);
            }
            e.Handled = true;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.VideoRequested -= OnVideoRequested;
            viewModel.ClipboardLinkFound -= OnClipboardLinkFound;
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _tray?.Dispose();
        _tray = null;
    }

    /// <summary>Offers to download a link that was copied to the clipboard.</summary>
    private void OnClipboardLinkFound(object? sender, string url)
    {
        var answer = MessageBox.Show(
            this,
            $"A link was copied to the clipboard:\n\n{url}\n\nDownload it?",
            "ADM",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes || DataContext is not MainViewModel viewModel)
            return;

        if (viewModel.TryRequestVideo(url))
            return;

        viewModel.AddUrls(url);
    }

    /// <summary>Keeps the tray tooltip in step with the download list.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.SummaryText) || _tray is null)
            return;

        if (DataContext is not MainViewModel viewModel)
            return;

        _tray.SetText(viewModel.SummaryText);
    }

    /// <summary>Brings the window back from the notification area.</summary>
    public void RestoreFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;

            if (!IsVisible)
                Show();

            Activate();
            Topmost = true;
            Topmost = false;
        });
    }

    private void ShowFromTray() => RestoreFromTray();

    private void PauseAllFromTray()
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.PauseAllCommand.Execute(null);
    }

    private void ResumeAllFromTray()
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.ResumeAllCommand.Execute(null);
    }

    private void ExitFromTray()
    {
        Dispatcher.Invoke(() => _ = ExitAsync());
    }

    /// <summary>Checks for a newer ADM release and offers the download page.</summary>
    private void CheckForUpdatesFromTray()
    {
        Dispatcher.Invoke(() => _ = CheckForUpdatesAsync());
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var service = new AppUpdateService();
            var update = await service.CheckAsync();

            if (update is null)
            {
                MessageBox.Show(this, "ADM is up to date.", "ADM update",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var answer = MessageBox.Show(this,
                $"ADM {update.Version} is available.\n\n{update.Notes}\n\nOpen the download page?",
                "ADM update", MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (answer == MessageBoxResult.Yes)
                AppUpdateService.OpenDownload(update);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The update check failed:\n\n" + ex.Message, "ADM update",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnVideoRequested(object? sender, string url) =>
        OpenVideoDownload(url);

    private void OnAddBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        if (e.Key == Key.Enter)
        {
            viewModel.AddFromInput();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            viewModel.AddUrlText = string.Empty;
            e.Handled = true;
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        var dialog = new SettingsWindow(viewModel.Settings) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            viewModel.ApplySettings();
            StartupRegistration.Apply(viewModel.Settings.StartWithWindows);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.Text) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || e.Data.GetData(DataFormats.Text) is not string text)
            return;

        if (!viewModel.TryRequestVideo(text))
            viewModel.AddUrls(text);
    }

    /// <summary>
    /// Closing the window only hides it. ADM keeps running in the notification
    /// area so downloads continue and the browser extension stays reachable.
    /// </summary>
    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested || _shutdownStarted)
            return;

        e.Cancel = true;
        Hide();

        // Mention the tray once per session; the balloon would be noise after that.
        if (!_trayNoticeShown)
        {
            _trayNoticeShown = true;
            _tray?.ShowNotice(
                "ADM is still running",
                "Downloads continue in the notification area. Right-click the ADM icon to exit.");
        }
    }

    /// <summary>Really quits the app: pauses the downloads and shuts down.</summary>
    private async Task ExitAsync()
    {
        if (_shutdownStarted)
            return;

        if (DataContext is MainViewModel viewModel && viewModel.HasActiveDownloads)
        {
            var answer = MessageBox.Show(
                "Downloads are still running. Pause them and exit?",
                "Exit ADM",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
                return;
        }

        _exitRequested = true;
        _shutdownStarted = true;

        try
        {
            if (DataContext is MainViewModel model)
                await model.ShutdownAsync();
        }
        catch (Exception)
        {
            // Exit regardless; unfinished state is already persisted periodically.
        }

        Application.Current.Shutdown();
    }

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
