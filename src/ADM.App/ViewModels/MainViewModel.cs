using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Adm.App.Infrastructure;
using Adm.App.Services;
using Adm.Core;
using Adm.Core.Media;
using Adm.Core.Models;
using Adm.Core.Net;
using Adm.Core.Settings;
using Adm.Core.Util;

namespace Adm.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly Regex UrlPattern = new(
        @"https?://[^\s""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FtpPattern = new(
        @"ftps?://[^\s""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly DownloadManager _manager;
    private readonly SettingsStore _settingsStore;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _liveTimer;
    private readonly ClipboardMonitor _clipboard;
    private NotificationService? _notifications;

    private string _addUrlText = string.Empty;
    private string _statusMessage = "Ready";
    private string _searchText = string.Empty;
    private bool _refreshQueued;
    private bool _disposed;
    private bool _exitingAfterQueue;

    public MainViewModel(DownloadManager manager, SettingsStore settingsStore)
    {
        _manager = manager;
        _settingsStore = settingsStore;
        _dispatcher = Dispatcher.CurrentDispatcher;

        DownloadsView = CollectionViewSource.GetDefaultView(_manager.Items);

        Categories = new ObservableCollection<CategoryItem>
        {
            new("All", "all"),
            new("Active", "active"),
            new("Completed", "completed"),
            new("Paused", "paused"),
            new("Video", "video"),
            new("Music", "music"),
            new("Documents", "documents"),
            new("Programs", "programs"),
            new("Archives", "archives"),
            new("Images", "images"),
            new("Other", "other")
        };
        Categories[0].IsSelected = true;
        DownloadsView.Filter = FilterDownload;

        AddCommand = new RelayCommand(_ => AddFromInput());
        PauseCommand = new RelayCommand(parameter => PauseItems(parameter));
        ResumeCommand = new RelayCommand(parameter => ResumeItems(parameter));
        RemoveCommand = new RelayCommand(parameter => RemoveItems(parameter));
        OpenFileCommand = new RelayCommand(parameter => OpenFile(parameter as DownloadItem));
        OpenFolderCommand = new RelayCommand(parameter => OpenFolder(parameter as DownloadItem));
        PauseAllCommand = new RelayCommand(_ => PauseAll());
        ResumeAllCommand = new RelayCommand(_ => ResumeAll());
        ClearCompletedCommand = new RelayCommand(_ => ClearCompleted());
        SelectCategoryCommand = new RelayCommand(parameter =>
        {
            if (parameter is CategoryItem category)
                SelectCategory(category);
        });
        CopyUrlCommand = new RelayCommand(parameter => CopyUrl(ResolveItems(parameter)));
        MoveToTopCommand = new RelayCommand(parameter => MoveToTop(ResolveItems(parameter)));
        RestartCommand = new RelayCommand(parameter => _ = RestartItemsAsync(ResolveItems(parameter)));
        ShowOptionsCommand = new RelayCommand(parameter => ShowOptions(ResolveItems(parameter)));

        _manager.ItemsChanged += OnItemsChanged;
        _manager.ItemUpdated += OnItemUpdated;
        _manager.ItemFinished += OnItemFinished;

        _liveTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _liveTimer.Tick += (_, _) =>
        {
            UpdateCounts();
            Raise(nameof(SummaryText));
            Raise(nameof(HasActiveDownloads));
            CheckScheduler();
        };
        _liveTimer.Start();

        _clipboard = new ClipboardMonitor();
        _clipboard.LinkFound += OnClipboardLink;
        ApplyClipboardMonitoring();

        UpdateCounts();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the user submits a video page and a format picker is needed.</summary>
    public event EventHandler<string>? VideoRequested;

    /// <summary>Raised when a link is found on the clipboard and can be queued.</summary>
    public event EventHandler<string>? ClipboardLinkFound;

    public ObservableCollection<CategoryItem> Categories { get; }

    /// <summary>The rows the user selected in the list.</summary>
    public ObservableCollection<DownloadItem> Selection { get; } = new();

    public ICollectionView DownloadsView { get; }

    public AppSettings Settings => _manager.Settings;

    public YtDlpService VideoService => _manager.VideoService;

    public ICommand AddCommand { get; }

    public ICommand PauseCommand { get; }

    public ICommand ResumeCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand OpenFileCommand { get; }

    public ICommand OpenFolderCommand { get; }

    public ICommand PauseAllCommand { get; }

    public ICommand ResumeAllCommand { get; }

    public ICommand ClearCompletedCommand { get; }

    public ICommand SelectCategoryCommand { get; }

    public ICommand CopyUrlCommand { get; }

    public ICommand MoveToTopCommand { get; }

    public ICommand RestartCommand { get; }

    public ICommand ShowOptionsCommand { get; }

    public int ItemCount => _manager.Items.Count;

    public bool IsEmpty => _manager.Items.Count == 0;

    public bool IsFilterEmpty => !IsEmpty && DownloadsView.IsEmpty;

    public bool HasActiveDownloads =>
        _manager.Items.Any(item => item.Status is DownloadStatus.Queued or DownloadStatus.Connecting or DownloadStatus.Downloading);

    /// <summary>Free text filter over the visible list.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value))
            {
                DownloadsView.Refresh();
                Raise(nameof(IsFilterEmpty));
            }
        }
    }

    public string AddUrlText
    {
        get => _addUrlText;
        set
        {
            if (Set(ref _addUrlText, value))
                Raise(nameof(AddUrlText));
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => Set(ref _statusMessage, value);
    }

    public string SummaryText
    {
        get
        {
            var items = _manager.Items;
            if (items.Count == 0)
                return "No downloads";

            int active = items.Count(item =>
                item.Status is DownloadStatus.Queued or DownloadStatus.Connecting or DownloadStatus.Downloading);

            double speed = items.Where(item => item.IsActive).Sum(item => item.BytesPerSecond);

            if (active == 0)
            {
                int completed = items.Count(item => item.Status == DownloadStatus.Completed);
                string noun = items.Count == 1 ? "download" : "downloads";
                return $"{items.Count} {noun} · {completed} completed";
            }

            return speed >= 1 ? $"{active} active · {Format.Speed(speed)}" : $"{active} active";
        }
    }

    /// <summary>Adds every URL found in the given text and returns how many were queued.</summary>
    public int AddUrls(string text)
    {
        var urls = ExtractUrls(text);

        if (urls.Count == 0)
        {
            StatusMessage = "Paste a valid http or https link.";
            return 0;
        }

        int added = 0;
        foreach (var url in urls)
        {
            try
            {
                _manager.Add(url);
                added++;
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
            }
        }

        if (added > 0)
            StatusMessage = added == 1 ? "Download queued." : $"{added} downloads queued.";

        return added;
    }

    public void AddFromInput()
    {
        if (TryRequestVideo(AddUrlText))
            return;

        if (AddUrls(AddUrlText) > 0)
            AddUrlText = string.Empty;
    }

    /// <summary>
    /// If the input is a single video page, asks the window to open the format
    /// picker instead of downloading the HTML page through the HTTP engine.
    /// </summary>
    public bool TryRequestVideo(string? text)
    {
        var urls = ExtractUrls(text);
        if (urls.Count != 1)
            return false;

        string url = urls[0];
        if (!VideoUrlDetector.IsVideoPage(url))
        {
            // A direct media file is handled by the HTTP engine; other pages
            // of a supported site are still worth probing.
            if (VideoUrlDetector.IsDirectMediaFile(url) ||
                !VideoUrlDetector.IsKnownVideoSite(url))
            {
                return false;
            }
        }

        AddUrlText = string.Empty;
        StatusMessage = "Choose a video format.";
        VideoRequested?.Invoke(this, url);
        return true;
    }

    /// <summary>Queues a video after the picker selected a format.</summary>
    public DownloadItem? AddVideo(VideoInfo info, VideoFormat format)
    {
        try
        {
            var item = _manager.AddVideo(info, format);
            StatusMessage = format.IsAudioOnly
                ? $"{format.ResolutionText} audio queued."
                : $"{format.ResolutionText} video queued.";
            return item;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Queues the videos of a playlist. Each entry is queued with the best
    /// available quality; the format can still be changed per video later.
    /// </summary>
    public int AddPlaylistEntries(IEnumerable<PlaylistEntry> entries)
    {
        int added = 0;

        foreach (var entry in entries)
        {
            try
            {
                var info = new VideoInfo
                {
                    PageUrl = entry.Url,
                    Id = entry.Id,
                    Title = entry.Title
                };

                var format = new VideoFormat
                {
                    Selector = "bestvideo+bestaudio/best",
                    ResolutionText = "Best available",
                    FormatText = "MP4 · best quality",
                    Extension = "mp4",
                    MergeContainer = "mp4"
                };

                if (_manager.AddVideo(info, format) is not null)
                    added++;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Warn($"Could not queue {entry.Title}.", ex);
            }
        }

        StatusMessage = added == 0
            ? "No playlist video could be queued."
            : added == 1 ? "1 video queued." : $"{added} videos queued.";

        return added;
    }

    /// <summary>Entry point used by the browser extension through the named pipe.</summary>
    public DownloadItem? AddFromBrowser(string url, string? fileName, string? referrer, string? userAgent,
        string? cookie = null)
    {
        try
        {
            var name = string.IsNullOrWhiteSpace(fileName) ? null : Path.GetFileName(fileName);
            var item = _manager.Add(url, new AddOptions
            {
                FileName = name,
                Referrer = referrer,
                UserAgent = userAgent,
                Cookie = cookie
            });

            StatusMessage = "Download queued.";
            return item;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            return null;
        }
    }

    public void ApplySettings()
    {
        try
        {
            _manager.ApplySettings();
            _settingsStore.Save(_manager.Settings);

            // The network settings feed the video engine too.
            VideoService.ProxyUrl = Settings.ProxyUrl;
            VideoService.CookiesFromBrowser = Settings.PassCookiesToYtDlp
                ? Settings.CookiesFromBrowser
                : null;

            ThemeManager.Apply(Settings.DarkTheme);
            ApplyClipboardMonitoring();

            StatusMessage = "Settings saved.";
            RefreshAll();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    /// <summary>Connects the notification service so finished downloads are announced.</summary>
    public void AttachNotifications(NotificationService? notifications) => _notifications = notifications;

    /// <summary>Starts or stops the clipboard watcher according to the settings.</summary>
    public void ApplyClipboardMonitoring()
    {
        if (Settings.MonitorClipboard)
        {
            _clipboard.Start();
        }
        else
        {
            _clipboard.Stop();
        }
    }

    public async Task ShutdownAsync()
    {
        _liveTimer.Stop();
        _clipboard.Stop();
        await _manager.ShutdownAsync();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _liveTimer.Stop();
        _clipboard.LinkFound -= OnClipboardLink;
        _clipboard.Dispose();
        _manager.ItemsChanged -= OnItemsChanged;
        _manager.ItemUpdated -= OnItemUpdated;
        _manager.ItemFinished -= OnItemFinished;
    }

    private void SelectCategory(CategoryItem category)
    {
        foreach (var item in Categories)
            item.IsSelected = ReferenceEquals(item, category);

        RefreshAll();
    }

    private bool FilterDownload(object obj)
    {
        if (obj is not DownloadItem item)
            return false;

        // The free text search narrows whatever the category filter showed.
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            string needle = SearchText.Trim();
            bool matches = item.FileName.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                           item.Url.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                           item.Category.Contains(needle, StringComparison.OrdinalIgnoreCase);

            if (!matches)
                return false;
        }

        string key = Categories.FirstOrDefault(category => category.IsSelected)?.Key ?? "all";

        return key switch
        {
            "all" => true,
            "active" => item.Status is DownloadStatus.Queued or DownloadStatus.Connecting or DownloadStatus.Downloading,
            "completed" => item.Status == DownloadStatus.Completed,
            "paused" => item.Status is DownloadStatus.Paused or DownloadStatus.Failed,
            _ => string.Equals(item.Category, key, StringComparison.OrdinalIgnoreCase)
        };
    }

    private void UpdateCounts()
    {
        var items = _manager.Items;
        int active = items.Count(item =>
            item.Status is DownloadStatus.Queued or DownloadStatus.Connecting or DownloadStatus.Downloading);

        foreach (var category in Categories)
        {
            category.Count = category.Key switch
            {
                "all" => items.Count,
                "active" => active,
                "completed" => items.Count(item => item.Status == DownloadStatus.Completed),
                "paused" => items.Count(item => item.Status is DownloadStatus.Paused or DownloadStatus.Failed),
                _ => items.Count(item => string.Equals(item.Category, category.Name, StringComparison.OrdinalIgnoreCase))
            };
        }
    }

    private void OnItemsChanged(object? sender, EventArgs e) => PostRefresh();

    private void OnItemUpdated(object? sender, DownloadItem item) => PostRefresh();

    /// <summary>Announces a finished or failed download.</summary>
    private void OnItemFinished(object? sender, DownloadItem item)
    {
        PostRefresh();

        bool succeeded = item.Status == DownloadStatus.Completed;

        if (succeeded && Settings.OpenWhenFinished && File.Exists(item.FilePath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Warn($"Could not open {item.FileName}.", ex);
            }
        }

        if (Settings.NotifyOnComplete)
            _notifications?.NotifyDownloadFinished(item.FileName, succeeded, item.ErrorMessage);

        if (succeeded)
            DiagnosticsLog.Info($"Finished {item.FileName} ({item.BytesDownloaded} bytes).");

        CheckExitWhenFinished();
    }

    /// <summary>Honours the "exit when the queue is empty" setting.</summary>
    private void CheckExitWhenFinished()
    {
        if (!Settings.ExitWhenFinished || _exitingAfterQueue)
            return;

        if (HasActiveDownloads)
            return;

        _exitingAfterQueue = true;
        _dispatcher.BeginInvoke(new Action(() => Application.Current.Shutdown()));
    }

    /// <summary>Applies the scheduled stop time and re-checks the start time.</summary>
    private void CheckScheduler()
    {
        if (_manager.ApplyScheduledStop())
        {
            StatusMessage = "Downloads paused by the schedule.";
            _manager.RefreshSchedule();
        }
    }

    private void OnClipboardLink(object? sender, string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        // The window asks the user before anything is queued.
        ClipboardLinkFound?.Invoke(this, url);
    }

    private void PostRefresh()
    {
        if (_disposed)
            return;

        if (_dispatcher.CheckAccess())
        {
            RefreshAll();
            return;
        }

        if (_refreshQueued)
            return;

        _refreshQueued = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _refreshQueued = false;
            RefreshAll();
        }));
    }

    private void RefreshAll()
    {
        UpdateCounts();
        DownloadsView.Refresh();
        Raise(nameof(ItemCount));
        Raise(nameof(SummaryText));
        Raise(nameof(IsEmpty));
        Raise(nameof(IsFilterEmpty));
        Raise(nameof(HasActiveDownloads));
    }

    private async void PauseAll()
    {
        await _manager.PauseAllAsync();
        StatusMessage = "All downloads paused.";
    }

    private async void ResumeAll()
    {
        await _manager.ResumeAllAsync();
        StatusMessage = "Resuming downloads.";
    }

    private async void ClearCompleted()
    {
        var completed = _manager.Items.Where(item => item.Status == DownloadStatus.Completed).ToList();
        foreach (var item in completed)
            await _manager.RemoveAsync(item, deleteFileFromDisk: false);

        StatusMessage = completed.Count == 0
            ? "There are no completed downloads to clear."
            : $"Cleared {completed.Count} completed downloads.";
    }

    /// <summary>
    /// Resolves the items a command applies to. The row buttons pass a single
    /// item; the context menu passes the whole selection.
    /// </summary>
    private List<DownloadItem> ResolveItems(object? parameter)
    {
        if (parameter is DownloadItem item)
            return new List<DownloadItem> { item };

        if (parameter is IEnumerable<DownloadItem> items)
            return items.ToList();

        return Selection.ToList();
    }

    private void PauseItems(object? parameter)
    {
        foreach (var item in ResolveItems(parameter))
            _manager.Pause(item);

        StatusMessage = "Paused.";
    }

    private void ResumeItems(object? parameter)
    {
        foreach (var item in ResolveItems(parameter))
            _manager.Resume(item);

        StatusMessage = "Resuming downloads.";
    }

    private async void RemoveItems(object? parameter)
    {
        var items = ResolveItems(parameter);
        if (items.Count == 0)
            return;

        bool deleteFiles = false;

        bool anyCompleted = items.Any(item => item.Status == DownloadStatus.Completed && File.Exists(item.FilePath));
        if (anyCompleted && _manager.Settings.ConfirmBeforeDelete)
        {
            var answer = MessageBox.Show(
                items.Count == 1
                    ? $"Delete \"{items[0].FileName}\" from disk as well?"
                    : $"Delete {items.Count} downloaded files from disk as well?",
                "Remove download",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel)
                return;

            deleteFiles = answer == MessageBoxResult.Yes;
        }

        try
        {
            foreach (var item in items)
                await _manager.RemoveAsync(item, deleteFiles);

            StatusMessage = items.Count == 1 ? "Removed from list." : $"Removed {items.Count} downloads.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async void RemoveItem(DownloadItem? item)
    {
        if (item is null)
            return;

        bool deleteFile = false;

        if (item.Status == DownloadStatus.Completed && File.Exists(item.FilePath) && _manager.Settings.ConfirmBeforeDelete)
        {
            var answer = MessageBox.Show(
                $"Delete \"{item.FileName}\" from disk as well?",
                "Remove download",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel)
                return;

            deleteFile = answer == MessageBoxResult.Yes;
        }

        try
        {
            await _manager.RemoveAsync(item, deleteFile);
            StatusMessage = "Removed from list.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task RestartItemsAsync(List<DownloadItem> items)
    {
        if (items.Count == 0)
            return;

        try
        {
            foreach (var item in items)
                await _manager.RestartAsync(item);

            StatusMessage = items.Count == 1 ? "Download restarted." : $"{items.Count} downloads restarted.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    /// <summary>Opens the per-download options window.</summary>
    private void ShowOptions(List<DownloadItem> items)
    {
        var item = items.FirstOrDefault();
        if (item is null)
            return;

        var dialog = new Views.DownloadOptionsWindow(item) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() == true)
        {
            StatusMessage = "Download options saved.";
            _manager.SetPriority(item, item.Priority);
        }
    }

    private void MoveToTop(List<DownloadItem> items)
    {
        foreach (var item in items)
            _manager.MoveToTop(item);
    }

    private void CopyUrl(List<DownloadItem> items)
    {
        var item = items.FirstOrDefault();
        if (item is null)
            return;

        try
        {
            Clipboard.SetText(item.Url);
            StatusMessage = "Link copied.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private void OpenFile(DownloadItem? item)
    {
        if (item is null || !File.Exists(item.FilePath))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private void OpenFolder(DownloadItem? item)
    {
        if (item is null)
            return;

        try
        {
            if (File.Exists(item.FilePath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FilePath}\"")
                {
                    UseShellExecute = true
                });
                return;
            }

            var folder = string.IsNullOrWhiteSpace(item.Folder) ? _manager.Settings.DownloadFolder : item.Folder;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private static List<string> ExtractUrls(string? text)
    {
        var matches = UrlPattern.Matches(text ?? string.Empty);
        var urls = matches
            .Select(match => match.Value.TrimEnd('.', ',', ';', ')', ']'))
            .Where(url => url.Length > 8)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // FTP addresses are not matched by the http pattern.
        foreach (Match match in FtpPattern.Matches(text ?? string.Empty))
        {
            string url = match.Value.TrimEnd('.', ',', ';', ')', ']');
            if (url.Length > 8 && !urls.Contains(url, StringComparer.OrdinalIgnoreCase))
                urls.Add(url);
        }

        if (urls.Count == 0)
        {
            var candidate = (text ?? string.Empty).Trim();
            if (candidate.Length > 3 && candidate.Contains('.') && !candidate.Contains(' '))
                urls.Add(candidate.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? candidate : "https://" + candidate);
        }

        return urls;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        Raise(propertyName);
        return true;
    }

    private void Raise(string? propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
