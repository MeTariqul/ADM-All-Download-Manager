using System.Collections.ObjectModel;
using System.ComponentModel;
using Adm.Core.Engine;
using Adm.Core.Media;
using Adm.Core.Models;
using Adm.Core.Net;
using Adm.Core.Settings;
using Adm.Core.Storage;
using Adm.Core.Util;

namespace Adm.Core;

/// <summary>
/// Public entry point used by the desktop app: owns the download list, the
/// concurrency queue and persistence.
/// </summary>
public sealed class DownloadManager : IDisposable
{
    private readonly AppSettings _settings;
    private readonly DownloadStore _store;
    private readonly YtDlpService _videoService;
    private readonly Dictionary<Guid, IDownloadTask> _tasks = new();
    private readonly HashSet<Guid> _running = new();
    private readonly object _gate = new();
    private readonly System.Threading.Timer _saveTimer;
    private readonly bool _ownsTimer;

    private int _dirty;
    private bool _shutdown;
    private bool _disposed;

    public DownloadManager(AppSettings settings, DownloadStore? store = null)
    {
        _settings = settings;
        _settings.Normalize();
        _store = store ?? new DownloadStore();
        _videoService = new YtDlpService();
        Items = new ObservableCollection<DownloadItem>();

        _saveTimer = new System.Threading.Timer(_ => Tick(), null, 2000, 2000);
        _ownsTimer = true;
    }

    public ObservableCollection<DownloadItem> Items { get; }

    public AppSettings Settings => _settings;

    public YtDlpService VideoService => _videoService;

    /// <summary>Raised when items are added, removed or re-ordered.</summary>
    public event EventHandler? ItemsChanged;

    /// <summary>Raised for every observable property change of an item (progress, status, ...).</summary>
    public event EventHandler<DownloadItem>? ItemUpdated;

    /// <summary>Raised after a download completes, fails or is paused.</summary>
    public event EventHandler<DownloadItem>? ItemFinished;

    public void Load()
    {
        List<DownloadItem> autoResume = new();

        lock (_gate)
        {
            foreach (var record in _store.Load().OrderBy(r => r.AddedAt))
            {
                bool wasActive = record.Status is DownloadStatus.Queued or DownloadStatus.Connecting
                    or DownloadStatus.Downloading;

                var item = record.ToItem();
                item.PropertyChanged += OnItemPropertyChanged;
                Items.Add(item);

                if (_settings.AutoResumeOnStartup && wasActive)
                    autoResume.Add(item);
            }
        }

        ItemsChanged?.Invoke(this, EventArgs.Empty);

        foreach (var item in autoResume)
        {
            item.Status = DownloadStatus.Queued;
            Pump();
        }
    }

    public DownloadItem Add(string url, AddOptions? options = null)
    {
        url = (url ?? string.Empty).Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            // FTP addresses are accepted as well.
            if (!FtpUrl.IsFtpUrl(url))
                throw new ArgumentException("Only http, https and ftp links can be downloaded.", nameof(url));
        }

        // The same link may already be in the list. The user's policy decides
        // whether that is a mistake or a deliberate second copy.
        var existing = FindDuplicate(url);
        if (existing is not null)
        {
            switch (_settings.DuplicatePolicy)
            {
                case DuplicatePolicy.Skip:
                    return existing;

                case DuplicatePolicy.Resume:
                    Resume(existing);
                    return existing;
            }
        }

        var folder = string.IsNullOrWhiteSpace(options?.Folder) ? _settings.DownloadFolder : options.Folder!;
        Directory.CreateDirectory(folder);

        string filePath = string.Empty;
        string category;
        if (!string.IsNullOrWhiteSpace(options?.FileName))
        {
            var safeName = FileNameResolver.Sanitize(options!.FileName!);
            filePath = FileNameResolver.UniquePath(folder, safeName);
            category = CategoryDetector.Detect(safeName);
        }
        else
        {
            category = CategoryDetector.Detect(FileNameResolver.FromUrl(url));
        }

        var item = new DownloadItem
        {
            Url = url,
            FilePath = filePath,
            Category = category,
            Kind = FtpUrl.IsFtpUrl(url) ? DownloadKind.Ftp : DownloadKind.Http,
            MaxConnections = Math.Clamp(options?.Connections ?? _settings.MaxConnections, 1, SegmentPlanner.MaximumConnections),
            SpeedLimitBytesPerSecond = Math.Max(0, options?.SpeedLimitBytesPerSecond ?? _settings.SpeedLimitBytesPerSecond),
            Referrer = options?.Referrer,
            UserAgent = string.IsNullOrWhiteSpace(options?.UserAgent) ? _settings.UserAgent : options.UserAgent,
            Cookie = options?.Cookie,
            Authorization = options?.Authorization,
            Priority = options?.Priority ?? 0,
            ExpectedChecksum = options?.ExpectedChecksum,
            ChecksumAlgorithm = options?.ChecksumAlgorithm ?? ChecksumAlgorithm.None,
            Status = DownloadStatus.Queued
        };

        item.PropertyChanged += OnItemPropertyChanged;

        lock (_gate)
        {
            Items.Insert(0, item);
        }

        SaveSoon();
        ItemsChanged?.Invoke(this, EventArgs.Empty);
        Pump();
        return item;
    }

    /// <summary>Finds an unfinished or completed download of the same URL.</summary>
    private DownloadItem? FindDuplicate(string url)
    {
        lock (_gate)
        {
            return Items.FirstOrDefault(item =>
                string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Queues a video/audio download using a format chosen in the picker. The
    /// output base name includes the site id so two qualities of the same video
    /// never overwrite each other.
    /// </summary>
    public DownloadItem AddVideo(VideoInfo info, VideoFormat format, AddOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(format);

        if (!Uri.TryCreate(info.PageUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("The video page URL is not valid.", nameof(info));
        }

        string folder = string.IsNullOrWhiteSpace(options?.Folder) ? _settings.DownloadFolder : options.Folder!;
        Directory.CreateDirectory(folder);

        string title = FileNameResolver.Sanitize(info.Title ?? string.Empty);
        if (string.IsNullOrWhiteSpace(title))
            title = "Video";
        if (title.Length > 100)
            title = title[..100].TrimEnd('.', ' ');

        string id = FileNameResolver.Sanitize(info.Id ?? string.Empty);
        if (id.Length > 40)
            id = id[..40];
        if (string.Equals(id, "download.bin", StringComparison.OrdinalIgnoreCase))
            id = string.Empty;

        string baseName = string.IsNullOrWhiteSpace(options?.FileName)
            ? string.IsNullOrWhiteSpace(id) ? title : $"{title} [{id}]"
            : Path.GetFileNameWithoutExtension(FileNameResolver.Sanitize(options.FileName));
        string extension = string.IsNullOrWhiteSpace(format.Extension) ? "mp4" : format.Extension!;
        string filePath = UniqueVideoPath(folder, baseName, extension);

        var item = new DownloadItem
        {
            Kind = DownloadKind.Video,
            Url = info.PageUrl,
            FilePath = filePath,
            Category = format.IsAudioOnly ? "Music" : "Video",
            TotalBytes = Math.Max(0, format.EstimatedBytes),
            MaxConnections = Math.Clamp(options?.Connections ?? _settings.MaxConnections, 1, SegmentPlanner.MaximumConnections),
            SpeedLimitBytesPerSecond = Math.Max(0, options?.SpeedLimitBytesPerSecond ?? _settings.SpeedLimitBytesPerSecond),
            Referrer = options?.Referrer,
            UserAgent = string.IsNullOrWhiteSpace(options?.UserAgent) ? _settings.UserAgent : options.UserAgent,
            Status = DownloadStatus.Queued,
            VideoSelector = format.Selector,
            VideoFormatLabel = string.IsNullOrWhiteSpace(format.FormatText)
                ? format.ResolutionText
                : $"{format.ResolutionText} · {format.FormatText}",
            VideoMergeContainer = format.MergeContainer,
            VideoAudioOnly = format.IsAudioOnly,
            VideoConvertToMp3 = format.ConvertToMp3,
            ExpectedChecksum = options?.ExpectedChecksum,
            ChecksumAlgorithm = options?.ChecksumAlgorithm ?? ChecksumAlgorithm.None
        };

        item.PropertyChanged += OnItemPropertyChanged;

        lock (_gate)
        {
            Items.Insert(0, item);
        }

        SaveSoon();
        ItemsChanged?.Invoke(this, EventArgs.Empty);
        Pump();
        return item;
    }

    public async Task PauseAsync(DownloadItem item)
    {
        IDownloadTask? task;
        lock (_gate)
            _tasks.TryGetValue(item.Id, out task);

        if (task is not null)
        {
            await task.PauseAsync().ConfigureAwait(false);
        }
        else if (item.Status == DownloadStatus.Queued)
        {
            item.Status = DownloadStatus.Paused;
        }

        SaveSoon();
        Pump();
    }

    public void Pause(DownloadItem item) => _ = PauseAsync(item);

    public void Resume(DownloadItem item)
    {
        if (item.Status is DownloadStatus.Paused or DownloadStatus.Failed)
        {
            item.ErrorMessage = null;
            item.Status = DownloadStatus.Queued;

            // A manual resume starts right away, without waiting for a backoff.
            item.RetryNotBefore = DateTimeOffset.MinValue;

            SaveSoon();
            Pump();
        }
    }

    public async Task ResumeAllAsync()
    {
        foreach (var item in Items.Where(i => i.Status is DownloadStatus.Paused or DownloadStatus.Failed))
            item.Status = DownloadStatus.Queued;

        SaveSoon();
        Pump();
        await Task.CompletedTask;
    }

    public async Task PauseAllAsync()
    {
        List<IDownloadTask> tasks;
        lock (_gate)
            tasks = _tasks.Values.ToList();

        await Task.WhenAll(tasks.Select(t => t.PauseAsync())).ConfigureAwait(false);

        foreach (var item in Items.Where(i => i.Status == DownloadStatus.Queued))
            item.Status = DownloadStatus.Paused;

        SaveSoon();
        await Task.CompletedTask;
    }

    public async Task RemoveAsync(DownloadItem item, bool deleteFileFromDisk = true)
    {
        IDownloadTask? task;
        lock (_gate)
            _tasks.TryGetValue(item.Id, out task);

        item.PropertyChanged -= OnItemPropertyChanged;

        lock (_gate)
            Items.Remove(item);

        ItemsChanged?.Invoke(this, EventArgs.Empty);

        if (task is not null)
            await task.PauseAsync().ConfigureAwait(false);

        if (item.Kind == DownloadKind.Video)
        {
            YtDlpDownloadTask.DeleteArtifacts(item, deleteFileFromDisk);
        }
        else
        {
            TryDelete(item.TempPath);
            if (deleteFileFromDisk)
                TryDelete(item.FilePath);
        }
        SaveSoon();
        Pump();
    }

    public void Remove(DownloadItem item, bool deleteFileFromDisk = true) =>
        _ = RemoveAsync(item, deleteFileFromDisk);

    /// <summary>Moves a download to the top of the list so it is easier to find.</summary>
    public void MoveToTop(DownloadItem item)
    {
        lock (_gate)
        {
            int index = Items.IndexOf(item);
            if (index <= 0)
                return;

            Items.Move(index, 0);
        }

        SaveSoon();
        ItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Changes the queue priority of a download.</summary>
    public void SetPriority(DownloadItem item, int priority)
    {
        item.Priority = priority;
        SaveSoon();
        ItemUpdated?.Invoke(this, item);
    }

    /// <summary>
    /// Starts a download over from the beginning: the partial data is removed
    /// and the item is queued again.
    /// </summary>
    public async Task RestartAsync(DownloadItem item)
    {
        await PauseAsync(item).ConfigureAwait(false);

        try
        {
            TryDelete(item.TempPath);
        }
        catch (Exception)
        {
            // A locked file is cleaned up when the item is removed.
        }

        item.Segments.Clear();
        item.BytesDownloaded = 0;
        item.RetryCount = 0;
        item.ErrorMessage = null;
        item.ChecksumVerified = null;

        // The name is resolved again from the server, so a stale file name
        // does not block the restart.
        if (item.Kind == DownloadKind.Http)
            item.FilePath = string.Empty;

        item.Status = DownloadStatus.Queued;
        SaveSoon();
        ItemUpdated?.Invoke(this, item);
        Pump();
    }

    public void ApplySettings()
    {
        _settings.Normalize();

        lock (_gate)
        {
            foreach (var item in Items)
            {
                item.SpeedLimitBytesPerSecond = _settings.SpeedLimitBytesPerSecond;
                item.MaxConnections = _settings.MaxConnections;
            }

            foreach (var task in _tasks.Values)
                task.SetSpeedLimit(_settings.SpeedLimitBytesPerSecond);
        }

        SaveSoon();
        Pump();
    }

    public async Task ShutdownAsync()
    {
        _shutdown = true;
        _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);

        List<IDownloadTask> tasks;
        lock (_gate)
            tasks = _tasks.Values.ToList();

        await Task.WhenAll(tasks.Select(t => t.PauseAsync())).ConfigureAwait(false);
        SaveNow();
    }

    private void Pump()
    {
        if (_shutdown)
            return;

        List<DownloadItem> toStart = new();

        lock (_gate)
        {
            int capacity = Math.Max(0, _settings.MaxConcurrentDownloads - _running.Count);
            if (capacity > 0)
            {
                // Higher priority first, then oldest first.
                foreach (var item in Items
                             .OrderByDescending(i => i.Priority)
                             .ThenBy(i => i.AddedAt))
                {
                    if (capacity == 0)
                        break;

                    if (item.Status != DownloadStatus.Queued || _running.Contains(item.Id))
                        continue;

                    if (!IsScheduledToStart(item))
                        continue;

                    // A retry waits for its backoff to expire.
                    if (item.RetryNotBefore > DateTimeOffset.Now)
                        continue;

                    _running.Add(item.Id);
                    toStart.Add(item);
                    capacity--;
                }
            }
        }

        foreach (var item in toStart)
            _ = RunItemAsync(item);
    }

    /// <summary>
    /// False while the scheduler is holding a download back, for example because
    /// the configured start time has not arrived yet.
    /// </summary>
    private bool IsScheduledToStart(DownloadItem item)
    {
        if (_settings.ScheduledStartTime is not { } start)
            return true;

        TimeSpan now = DateTime.Now.TimeOfDay;
        return now >= start;
    }

    /// <summary>
    /// Pauses every active download once the configured stop time is reached.
    /// Returns true when something was paused.
    /// </summary>
    public bool ApplyScheduledStop()
    {
        if (_settings.ScheduledStopTime is not { } stop)
            return false;

        TimeSpan now = DateTime.Now.TimeOfDay;
        bool insideWindow = stop < _settings.ScheduledStartTime
            ? now >= stop && now < _settings.ScheduledStartTime
            : now >= stop || now < _settings.ScheduledStartTime;

        if (!insideWindow)
            return false;

        var active = Items.Where(item => item.IsActive).ToList();
        if (active.Count == 0)
            return false;

        foreach (var item in active)
            Pause(item);

        return true;
    }

    /// <summary>Re-checks the scheduler after the clock passes a boundary.</summary>
    public void RefreshSchedule() => Pump();

    private async Task RunItemAsync(DownloadItem item)
    {
        bool shouldStart;
        lock (_gate)
            shouldStart = !_shutdown && item.Status == DownloadStatus.Queued;

        if (!shouldStart)
        {
            lock (_gate)
                _running.Remove(item.Id);

            Pump();
            return;
        }

        IDownloadTask task = item.Kind switch
        {
            DownloadKind.Video => new YtDlpDownloadTask(item, _settings, _videoService),
            DownloadKind.Ftp => new FtpDownloadTask(item, _settings),
            _ => new DownloadTask(item, _settings, SaveSoon)
        };

        lock (_gate)
            _tasks[item.Id] = task;

        try
        {
            // Refuse to start when the target drive cannot hold the file.
            string? spaceWarning = CheckDiskSpace(item);
            if (spaceWarning is not null)
            {
                item.Status = DownloadStatus.Failed;
                item.ErrorMessage = spaceWarning;
                DiagnosticsLog.Warn($"Download from {DiagnosticsLog.SafeEndpoint(item.Url)} stopped: {spaceWarning}");
                return;
            }

            await task.StartAsync().ConfigureAwait(false);

            // A download that failed but is set to retry goes back in the queue.
            if (item.Status == DownloadStatus.Failed && ShouldRetry(item))
                ScheduleRetry(item);
        }
        catch (Exception ex)
        {
            item.Status = DownloadStatus.Failed;
            item.ErrorMessage = ex.Message;
            // Exception messages from HTTP stacks can contain full URLs, including
            // signed query strings. Keep details in the UI item and log only the type.
            DiagnosticsLog.Error($"Download from {DiagnosticsLog.SafeEndpoint(item.Url)} failed ({ex.GetType().Name}).");

            if (ShouldRetry(item))
                ScheduleRetry(item);
        }
        finally
        {
            lock (_gate)
            {
                _tasks.Remove(item.Id);
                _running.Remove(item.Id);
            }

            SaveSoon();
            ItemFinished?.Invoke(this, item);
            Pump();
        }
    }

    /// <summary>True when the retry policy allows another attempt.</summary>
    private bool ShouldRetry(DownloadItem item) => _settings.RetryPolicy switch
    {
        RetryPolicy.None => false,
        RetryPolicy.Unlimited => true,
        _ => item.RetryCount < _settings.MaxRetries
    };

    /// <summary>
    /// Puts a failed download back in the queue after a growing backoff, so a
    /// server that is down is not hammered.
    /// </summary>
    private void ScheduleRetry(DownloadItem item)
    {
        item.RetryCount++;
        item.IsRetryScheduled = true;
        item.ErrorMessage = null;
        item.Status = DownloadStatus.Queued;

        int seconds = Math.Min(60, 1 << Math.Min(item.RetryCount, 6));
        item.RetryNotBefore = DateTimeOffset.Now.AddSeconds(seconds);

        DiagnosticsLog.Info(
            $"Retrying {DiagnosticsLog.SafeEndpoint(item.Url)} in {seconds} s (attempt {item.RetryCount + 1} of {_settings.MaxRetries + 1}).");
    }

    /// <summary>
    /// Checks the free space of the target drive. Returns a warning message
    /// when the download does not fit, or null when it does.
    /// </summary>
    private string? CheckDiskSpace(DownloadItem item)
    {
        if (!_settings.CheckDiskSpace)
            return null;

        string folder = string.IsNullOrWhiteSpace(item.Folder) ? _settings.DownloadFolder : item.Folder;
        long required = item.TotalBytes > 0 ? item.TotalBytes : 0;
        if (required <= 0)
            return null;

        return DiskSpace.Check(folder, required);
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        SaveSoon();

        if (sender is DownloadItem item && e.PropertyName == nameof(DownloadItem.Status))
            ItemUpdated?.Invoke(this, item);
    }

    private void SaveSoon() => Interlocked.Exchange(ref _dirty, 1);

    /// <summary>
    /// The periodic tick: it saves the list and re-checks the queue so a
    /// scheduled retry starts once its backoff has expired.
    /// </summary>
    private void Tick()
    {
        FlushIfDirty();
        Pump();
    }

    private void FlushIfDirty()
    {
        if (_disposed)
            return;

        if (Interlocked.Exchange(ref _dirty, 0) == 0)
            return;

        try
        {
            SaveNow();
        }
        catch (Exception)
        {
            SaveSoon(); // retry on the next tick
        }
    }

    private void SaveNow()
    {
        List<DownloadItem> snapshot;
        lock (_gate)
            snapshot = Items.ToList();

        _store.Save(snapshot);
    }

    /// <summary>
    /// Reserves an output base name for a video. Different extensions are not
    /// enough to guarantee uniqueness because yt-dlp may choose its own final
    /// container, so any file sharing the base name counts as a collision.
    /// </summary>
    private static string UniqueVideoPath(string folder, string baseName, string extension)
    {
        for (int counter = 1; counter < 10_000; counter++)
        {
            string stem = counter == 1 ? baseName : $"{baseName} ({counter})";
            bool taken;
            try
            {
                taken = Directory.Exists(folder) &&
                        Directory.EnumerateFiles(folder, stem + ".*").Any();
            }
            catch (Exception)
            {
                taken = false;
            }

            if (!taken)
                return Path.Combine(folder, stem + "." + extension);
        }

        return Path.Combine(folder, $"{baseName} ({Guid.NewGuid():N}).{extension}");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
            // A file in use can be cleaned up later; removal must still succeed.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_ownsTimer)
            _saveTimer.Dispose();
    }
}
