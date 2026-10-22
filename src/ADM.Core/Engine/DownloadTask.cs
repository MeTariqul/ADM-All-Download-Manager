using System.Net.Http.Headers;
using Adm.Core.Models;
using Adm.Core.Net;
using Adm.Core.Settings;
using Adm.Core.Util;

namespace Adm.Core.Engine;

/// <summary>
/// Runs one download: probes the URL, plans segments, supervises workers,
/// throttles bandwidth, reports progress and persists resume state.
/// One instance per run; create a new one to resume a paused download.
/// </summary>
public sealed class DownloadTask : IDownloadTask
{
    private readonly DownloadItem _item;
    private readonly AppSettings _settings;
    private readonly HttpClient _client;
    private readonly SpeedLimiter _limiter;
    private readonly List<Segment> _segments = new();
    private readonly Dictionary<Segment, Task> _workers = new();
    private readonly List<PendingSplit> _pendingSplits = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lifecycleLock = new();
    private readonly Action? _onHeartbeat;

    private TransferContext? _context;
    private Task? _runTask;
    private bool _streaming;
    private long _lastSampleBytes;
    private DateTime _lastSampleAt;
    private double _smoothedSpeed;

    public DownloadTask(DownloadItem item, AppSettings settings, Action? onHeartbeat = null)
    {
        _item = item;
        _settings = settings.Clone();
        _settings.Normalize();
        _onHeartbeat = onHeartbeat;

        _item.MaxConnections = Math.Clamp(_item.MaxConnections, 1, SegmentPlanner.MaximumConnections);
        _limiter = new SpeedLimiter(_item.SpeedLimitBytesPerSecond);
        _client = HttpProbe.CreateClient(_item.MaxConnections, _settings.ProxyUrl,
            _settings.ProxyUsername, _settings.ProxyPassword);
    }

    public DownloadItem Item => _item;

    /// <summary>Raised when the run ends (completed, paused or failed).</summary>
    public event EventHandler? Finished;

    public Task StartAsync()
    {
        lock (_lifecycleLock)
        {
            return _runTask ??= RunAsync();
        }
    }

    public void SetSpeedLimit(long bytesPerSecond) => _limiter.UpdateLimit(bytesPerSecond);

    /// <summary>Cancels the transfer; state is flushed so the download can resume later.</summary>
    public async Task PauseAsync()
    {
        Task? run;
        lock (_lifecycleLock)
        {
            _cts.Cancel();
            run = _runTask;
        }

        if (run is null)
        {
            _item.Status = DownloadStatus.Paused;
            return;
        }

        try
        {
            await run.ConfigureAwait(false);
        }
        catch
        {
            // RunAsync handles its own failures; nothing to do here.
        }
    }

    private async Task RunAsync()
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var cancellationToken = linkedCts.Token;

        try
        {
            if (!await TryRestoreAsync(cancellationToken).ConfigureAwait(false))
                await PrepareAsync(cancellationToken).ConfigureAwait(false);

            _item.Status = DownloadStatus.Downloading;
            _item.ErrorMessage = null;
            _lastSampleAt = DateTime.UtcNow;
            _lastSampleBytes = DownloadedBytes();
            _smoothedSpeed = 0;

            await TransferAsync(cancellationToken).ConfigureAwait(false);
            await CompleteAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            if (_item.Status != DownloadStatus.Completed)
            {
                _item.Status = DownloadStatus.Paused;
                _item.BytesPerSecond = 0;
                _item.RefreshProgress();
            }

            SnapshotSegments();
        }
        catch (Exception ex)
        {
            linkedCts.Cancel();

            if (_item.Status != DownloadStatus.Completed)
            {
                _item.Status = DownloadStatus.Failed;
                _item.ErrorMessage = ex.Message;
                _item.BytesPerSecond = 0;
                _item.RefreshProgress();
            }

            SnapshotSegments();
        }
        finally
        {
            // Never leave workers writing to the .admpart file after reporting an outcome.
            var running = _workers.Values.ToArray();
            if (running.Length > 0)
            {
                try { await Task.WhenAll(running).ConfigureAwait(false); }
                catch { /* primary failure already recorded */ }
            }

            _client.Dispose();
            _onHeartbeat?.Invoke();
            Finished?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task PrepareAsync(CancellationToken cancellationToken)
    {
        _item.Status = DownloadStatus.Connecting;

        var probe = await HttpProbe.ProbeAsync(_client, _item.Url, _item.UserAgent ?? _settings.UserAgent,
            _item.Referrer, cancellationToken, ResolveAuthorization(), _item.Cookie).ConfigureAwait(false);

        _item.SupportsRanges = probe.SupportsRanges && probe.TotalBytes > 0;
        _item.TotalBytes = Math.Max(0, probe.TotalBytes);
        _item.ETag = probe.ETag;
        _item.LastModified = probe.LastModified;
        _streaming = !_item.SupportsRanges;

        if (string.IsNullOrWhiteSpace(_item.FilePath))
        {
            var name = FileNameResolver.Resolve(probe.ContentDispositionFileName,
                probe.ContentDispositionFileNameStar, probe.FinalUrl, probe.ContentType);
            var folder = EnsureDownloadFolder();
            _item.FilePath = FileNameResolver.UniquePath(folder, name);
            _item.Category = CategoryDetector.Detect(name);
        }
        else
        {
            Directory.CreateDirectory(_item.Folder);
        }

        _segments.Clear();
        if (_streaming)
        {
            _segments.Add(new Segment(0, -1, 0));
        }
        else
        {
            foreach (var record in SegmentPlanner.Plan(_item.TotalBytes, _item.MaxConnections))
                _segments.Add(new Segment(record.Start, record.End, record.Position));
        }

        CreateTempFile();
        SnapshotSegments();
    }

    /// <summary>
    /// Restores a paused download when the partial file and segment map are still
    /// valid and the remote file has not changed.
    /// </summary>
    private async Task<bool> TryRestoreAsync(CancellationToken cancellationToken)
    {
        if (_item.Segments.Count == 0 || string.IsNullOrWhiteSpace(_item.FilePath))
            return false;

        if (_item.Segments.Any(s => s.End < 0))
            return false; // Streaming downloads cannot resume.

        if (!File.Exists(_item.TempPath))
            return false;

        var info = new FileInfo(_item.TempPath);
        if (info.Length != _item.TotalBytes || _item.TotalBytes <= 0)
            return false;

        var probe = await HttpProbe.ProbeAsync(_client, _item.Url, _item.UserAgent ?? _settings.UserAgent,
            _item.Referrer, cancellationToken, ResolveAuthorization(), _item.Cookie).ConfigureAwait(false);

        if (!probe.SupportsRanges || probe.TotalBytes != _item.TotalBytes)
            return false;

        if (!string.Equals(_item.ETag, probe.ETag, StringComparison.Ordinal) && probe.ETag is not null)
            return false;

        _streaming = false;
        _segments.Clear();
        foreach (var record in _item.Segments)
        {
            long end = record.End;
            long position = Math.Clamp(record.Position, record.Start, end + 1);
            _segments.Add(new Segment(record.Start, end, position));
        }

        if (_segments.Count == 0 || _segments.Sum(s => s.Remaining) == 0)
            return false;

        return true;
    }

    private async Task TransferAsync(CancellationToken cancellationToken)
    {
        _context = new TransferContext(_client, _item.Url, _item.TempPath,
            _item.UserAgent ?? _settings.UserAgent, _item.Referrer, ParseIfRange(_item.ETag),
            _limiter, MaxRetries: ResolveMaxRetries(),
            Authorization: ResolveAuthorization(), Cookie: _item.Cookie);

        foreach (var segment in _segments.Where(s => !s.IsComplete))
            _workers[segment] = SegmentWorker.RunAsync(segment, _context, cancellationToken);

        int ticks = 0;

        while (_workers.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);

            var faulted = _workers.FirstOrDefault(pair => pair.Value.IsFaulted);
            if (faulted.Value is not null)
            {
                await faulted.Value.ConfigureAwait(false); // rethrows the worker exception
            }

            foreach (var pair in _workers.Where(pair => pair.Value.IsCompleted).ToList())
                _workers.Remove(pair.Key);

            StartPendingSplits(cancellationToken);
            SplitBusySegments(cancellationToken);
            ReportProgress();

            if (++ticks % 8 == 0)
            {
                SnapshotSegments();
                _onHeartbeat?.Invoke();
            }
        }

        if (!_streaming)
        {
            long transferred = DownloadedBytes();
            if (transferred < _item.TotalBytes)
                throw new IOException("The download ended before all bytes were received.");
        }

        ReportProgress();
        SnapshotSegments();
    }

    /// <summary>
    /// When some connections finish early, halve the largest remaining segment.
    /// The old worker stops at the new boundary; the second half is handed to a
    /// fresh worker once the old one has actually stopped (prevents overlapping writes).
    /// </summary>
    private void SplitBusySegments(CancellationToken cancellationToken)
    {
        while (true)
        {
            int active = _workers.Count(pair => !pair.Value.IsCompleted);
            if (active + _pendingSplits.Count >= _item.MaxConnections)
                return;

            Segment? candidate = null;
            long largestRemaining = 0;

            foreach (var segment in _segments)
            {
                if (segment.End < 0)
                    continue;

                if (_pendingSplits.Any(p => ReferenceEquals(p.Segment, segment)))
                    continue;

                if (!_workers.TryGetValue(segment, out var worker) || worker.IsCompleted)
                    continue;

                long remaining = segment.Remaining;
                if (remaining > largestRemaining)
                {
                    largestRemaining = remaining;
                    candidate = segment;
                }
            }

            if (candidate is null || largestRemaining < SegmentPlanner.MinimumSegmentSize * 4)
                return;

            long? oldEnd = candidate.TryShrink(SegmentPlanner.MinimumSegmentSize * 2);
            if (oldEnd is null)
                return;

            _pendingSplits.Add(new PendingSplit(candidate, oldEnd.Value));
        }
    }

    private void StartPendingSplits(CancellationToken cancellationToken)
    {
        for (int i = _pendingSplits.Count - 1; i >= 0; i--)
        {
            var split = _pendingSplits[i];

            // Wait until the original worker has stopped writing.
            if (_workers.TryGetValue(split.Segment, out var worker) && !worker.IsCompleted)
                continue;

            _pendingSplits.RemoveAt(i);

            long start = split.Segment.Position;
            if (start > split.OldEnd)
                continue;

            if (split.OldEnd - start + 1 < SegmentPlanner.MinimumSegmentSize / 2)
                continue;

            var extra = new Segment(start, split.OldEnd, start);
            _segments.Add(extra);
            _workers[extra] = SegmentWorker.RunAsync(extra, _context!, cancellationToken);
        }
    }

    private void ReportProgress()
    {
        long bytes = DownloadedBytes();
        var now = DateTime.UtcNow;
        double seconds = (now - _lastSampleAt).TotalSeconds;

        if (seconds >= 0.2)
        {
            double instant = (bytes - _lastSampleBytes) / seconds;
            _smoothedSpeed = _smoothedSpeed <= 0 ? instant : (_smoothedSpeed * 0.6) + (instant * 0.4);
            _lastSampleBytes = bytes;
            _lastSampleAt = now;
        }

        _item.BytesDownloaded = bytes;
        _item.BytesPerSecond = Math.Max(0, _smoothedSpeed);
        _item.RefreshProgress();
    }

    private long DownloadedBytes() => _segments.Sum(segment => segment.Position - segment.Start);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
            // A file that is still open can be cleaned up later.
        }
    }

    private Task CompleteAsync()
    {
        if (!_streaming)
        {
            if (_item.BytesDownloaded < _item.TotalBytes)
                _item.BytesDownloaded = _item.TotalBytes;
        }
        else
        {
            var info = new FileInfo(_item.TempPath);
            _item.TotalBytes = info.Length;
            _item.BytesDownloaded = info.Length;
        }

        Directory.CreateDirectory(_item.Folder);

        string? finalPath = ResolveFinalPath();
        if (finalPath is null)
        {
            // The file already exists and the user asked to keep it.
            TryDelete(_item.TempPath);
            _item.CompletedAt = DateTimeOffset.Now;
            _item.BytesPerSecond = 0;
            _item.Segments.Clear();
            _item.Status = DownloadStatus.Completed;
            _item.RefreshProgress();
            return Task.CompletedTask;
        }

        File.Move(_item.TempPath, finalPath);
        _item.FilePath = finalPath;
        _item.CompletedAt = DateTimeOffset.Now;
        _item.BytesPerSecond = 0;
        _item.Segments.Clear();
        _item.Status = DownloadStatus.Completed;
        _item.RefreshProgress();

        VerifyChecksum(finalPath);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Applies the file-exists policy. Returns null when the download should be
    /// dropped because an identical file is already on disk.
    /// </summary>
    private string? ResolveFinalPath()
    {
        string finalPath = _item.FilePath;
        if (!File.Exists(finalPath))
            return finalPath;

        switch (_settings.FileExistsPolicy)
        {
            case FileExistsPolicy.Overwrite:
                TryDelete(finalPath);
                return finalPath;

            case FileExistsPolicy.Skip:
                return null;

            default:
                return FileNameResolver.UniquePath(_item.Folder, Path.GetFileName(finalPath));
        }
    }

    /// <summary>Compares the finished file with the expected hash, when one was given.</summary>
    private void VerifyChecksum(string path)
    {
        if (_item.ChecksumAlgorithm == ChecksumAlgorithm.None || string.IsNullOrWhiteSpace(_item.ExpectedChecksum))
        {
            _item.ChecksumVerified = null;
            return;
        }

        try
        {
            string actual = Checksum.Compute(path, _item.ChecksumAlgorithm);
            bool ok = Checksum.Matches(_item.ExpectedChecksum, actual);
            _item.ChecksumVerified = ok;

            if (ok)
            {
                DiagnosticsLog.Info($"Checksum verified for {Path.GetFileName(path)}.");
            }
            else
            {
                _item.ErrorMessage =
                    $"The file does not match the expected {Checksum.AlgorithmName(_item.ChecksumAlgorithm)} checksum.";
                DiagnosticsLog.Warn($"Checksum mismatch for {Path.GetFileName(path)}: expected {_item.ExpectedChecksum}, got {actual}.");
            }
        }
        catch (Exception ex)
        {
            _item.ChecksumVerified = null;
            DiagnosticsLog.Warn($"Could not verify the checksum of {Path.GetFileName(path)}.", ex);
        }
    }

    /// <summary>Number of transfer retries allowed by the current retry policy.</summary>
    private int ResolveMaxRetries() => _settings.RetryPolicy switch
    {
        RetryPolicy.None => 0,
        RetryPolicy.Unlimited => 50,
        _ => Math.Clamp(_settings.MaxRetries, 0, 50)
    };

    /// <summary>The Authorization header for this request, from the item or the settings.</summary>
    private string? ResolveAuthorization()
    {
        if (!string.IsNullOrWhiteSpace(_item.Authorization))
            return _item.Authorization;

        return NetworkOptions.BuildBasicAuth(_settings.BasicAuthUsername, _settings.BasicAuthPassword);
    }

    private void CreateTempFile()
    {
        using var stream = new FileStream(_item.TempPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        if (!_streaming && _item.TotalBytes > 0)
            stream.SetLength(_item.TotalBytes);
    }

    private string EnsureDownloadFolder()
    {
        var folder = _settings.DownloadFolder;
        try
        {
            Directory.CreateDirectory(folder);
            return folder;
        }
        catch (Exception)
        {
            folder = AppSettings.DefaultDownloadFolder();
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    private void SnapshotSegments()
    {
        _item.Segments = _segments
            .Select(segment => new SegmentRecord
            {
                Start = segment.Start,
                End = segment.End,
                Position = segment.Position
            })
            .ToList();
    }

    private static RangeConditionHeaderValue? ParseIfRange(string? etag)
    {
        if (string.IsNullOrWhiteSpace(etag))
            return null;

        return EntityTagHeaderValue.TryParse(etag, out var value) && !value.IsWeak
            ? new RangeConditionHeaderValue(value)
            : null;
    }

    private sealed record PendingSplit(Segment Segment, long OldEnd);
}
