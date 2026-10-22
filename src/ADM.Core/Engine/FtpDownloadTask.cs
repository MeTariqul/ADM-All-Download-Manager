using Adm.Core.Models;
using Adm.Core.Net;
using Adm.Core.Settings;
using Adm.Core.Util;

namespace Adm.Core.Engine;

/// <summary>
/// Downloads a file over FTP or FTPS. FTP has no range support, so the file is
/// transferred in one stream; it can be paused, and it resumes by asking the
/// server for the rest of the file with the REST command.
/// </summary>
public sealed class FtpDownloadTask : IDownloadTask
{
    private readonly DownloadItem _item;
    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    private long _speedLimit;
    private bool _paused;

    public FtpDownloadTask(DownloadItem item, AppSettings settings)
    {
        _item = item;
        _settings = settings.Clone();
        _settings.Normalize();
        _speedLimit = item.SpeedLimitBytesPerSecond > 0
            ? item.SpeedLimitBytesPerSecond
            : settings.SpeedLimitBytesPerSecond;
    }

    public async Task StartAsync()
    {
        if (_paused)
        {
            _item.Status = DownloadStatus.Paused;
            return;
        }

        if (!FtpUrl.TryParse(_item.Url, out var info))
            throw new InvalidOperationException("That is not a valid FTP address.");

        _item.ErrorMessage = null;
        _item.Status = DownloadStatus.Connecting;

        string folder = string.IsNullOrWhiteSpace(_item.Folder) ? _settings.DownloadFolder : _item.Folder;
        Directory.CreateDirectory(folder);

        if (string.IsNullOrWhiteSpace(_item.FilePath))
        {
            string name = FileNameResolver.Sanitize(FileNameResolver.FromUrl(_item.Url));
            _item.FilePath = FileNameResolver.UniquePath(folder, name);
            _item.Category = CategoryDetector.Detect(name);
        }

        long start = 0;
        if (File.Exists(_item.TempPath))
        {
            start = new FileInfo(_item.TempPath).Length;
            if (start > 0)
                _item.BytesDownloaded = start;
        }

        var client = new FtpClient(info.UrlHost, info.UrlPort, info.IsSecure, info.UserName, info.Password);

        try
        {
            await client.ConnectAsync(_cts.Token).ConfigureAwait(false);

            long total = await client.GetSizeAsync(info.Path, _cts.Token).ConfigureAwait(false);
            if (total > 0)
                _item.TotalBytes = total;

            _item.Status = DownloadStatus.Downloading;

            using var file = new FileStream(_item.TempPath, FileMode.OpenOrCreate, FileAccess.Write,
                FileShare.ReadWrite, 64 * 1024, FileOptions.Asynchronous);

            file.Position = start;

            DateTime lastSample = DateTime.UtcNow;
            long lastBytes = start;

            await client.DownloadAsync(info.Path, file, start, written =>
            {
                _item.BytesDownloaded = written;

                double seconds = (DateTime.UtcNow - lastSample).TotalSeconds;
                if (seconds >= 0.3)
                {
                    _item.BytesPerSecond = (written - lastBytes) / seconds;
                    lastSample = DateTime.UtcNow;
                    lastBytes = written;
                }

                _item.RefreshProgress();
            }, _cts.Token).ConfigureAwait(false);

            await file.FlushAsync(_cts.Token).ConfigureAwait(false);
            file.Close();

            Complete(folder);
        }
        catch (OperationCanceledException)
        {
            if (_item.Status is DownloadStatus.Queued or DownloadStatus.Connecting or DownloadStatus.Downloading)
                _item.Status = DownloadStatus.Paused;
        }
        catch (Exception ex)
        {
            if (_paused || _cts.IsCancellationRequested)
            {
                if (_item.Status is DownloadStatus.Queued or DownloadStatus.Connecting or DownloadStatus.Downloading)
                    _item.Status = DownloadStatus.Paused;
            }
            else
            {
                throw new IOException("The FTP transfer failed: " + ex.Message, ex);
            }
        }
        finally
        {
            _item.BytesPerSecond = 0;
            client.Dispose();
        }
    }

    private void Complete(string folder)
    {
        string finalPath = _item.FilePath;

        if (File.Exists(finalPath))
        {
            switch (_settings.FileExistsPolicy)
            {
                case FileExistsPolicy.Overwrite:
                    File.Delete(finalPath);
                    break;
                case FileExistsPolicy.Skip:
                    File.Delete(_item.TempPath);
                    MarkCompleted();
                    return;
                default:
                    finalPath = FileNameResolver.UniquePath(folder, Path.GetFileName(finalPath));
                    break;
            }
        }

        File.Move(_item.TempPath, finalPath);
        _item.FilePath = finalPath;
        _item.CompletedAt = DateTimeOffset.Now;
        MarkCompleted();
    }

    private void MarkCompleted()
    {
        _item.BytesPerSecond = 0;
        _item.Status = DownloadStatus.Completed;
        _item.RefreshProgress();
    }

    public async Task PauseAsync()
    {
        lock (_gate)
            _paused = true;

        _cts.Cancel();

        // Give the transfer a moment to notice the cancellation.
        await Task.Delay(150).ConfigureAwait(false);

        if (_item.Status is DownloadStatus.Queued or DownloadStatus.Connecting or DownloadStatus.Downloading)
            _item.Status = DownloadStatus.Paused;
    }

    public void SetSpeedLimit(long bytesPerSecond) => _speedLimit = Math.Max(0, bytesPerSecond);
}
