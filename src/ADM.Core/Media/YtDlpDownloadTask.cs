using System.Diagnostics;
using System.Globalization;
using System.Text;
using Adm.Core.Engine;
using Adm.Core.Models;
using Adm.Core.Settings;
using Adm.Core.Util;

namespace Adm.Core.Media;

/// <summary>
/// Runs one yt-dlp process and projects its progress into the normal
/// <see cref="DownloadItem"/> list. Pausing kills the process tree; resuming
/// starts a new process with <c>--continue</c> so partial fragments are reused.
/// </summary>
public sealed class YtDlpDownloadTask : IDownloadTask
{
    private readonly DownloadItem _item;
    private readonly AppSettings _settings;
    private readonly YtDlpService _service;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    private Process? _process;
    private string? _finalPath;
    private bool _paused;
    private long _speedLimit;

    public YtDlpDownloadTask(DownloadItem item, AppSettings settings, YtDlpService service)
    {
        _item = item;
        _settings = settings;
        _service = service;
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

        if (!_service.IsAvailable)
        {
            throw new YtDlpException(
                "The video engine (yt-dlp) is missing. Reinstall ADM or run tools\\Get-VideoTools.ps1, then try again.");
        }

        bool needsFfmpeg = !string.IsNullOrWhiteSpace(_item.VideoMergeContainer) ||
                           _item.VideoConvertToMp3;

        if (needsFfmpeg && !_service.HasFfmpeg)
            throw new YtDlpException("ffmpeg is required for this format. Install ffmpeg or reinstall ADM.");

        _item.ErrorMessage = null;
        _item.BytesPerSecond = 0;
        _item.Status = DownloadStatus.Connecting;

        string folder = Path.GetDirectoryName(_item.FilePath) ?? _settings.DownloadFolder;
        Directory.CreateDirectory(folder);

        string baseName = Path.GetFileNameWithoutExtension(_item.FilePath);
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "video";

        // '%' starts a field reference in yt-dlp's output template; the base
        // name is literal text, so escape it.
        string outputTemplate = Path.Combine(folder, baseName.Replace("%", "%%") + ".%(ext)s");

        ProcessStartInfo startInfo = BuildStartInfo(outputTemplate);

        try
        {
            using var process = new Process { StartInfo = startInfo };
            lock (_gate)
                _process = process;

            if (!process.Start())
                throw new YtDlpException("The video engine could not be started.");

            Task<string> stderrTask = process.StandardError.ReadToEndAsync();

            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
                HandleLine(line);

            await process.WaitForExitAsync().ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);

            if (_cts.IsCancellationRequested || _paused)
            {
                _item.BytesPerSecond = 0;
                if (_item.Status is DownloadStatus.Queued or DownloadStatus.Connecting or DownloadStatus.Downloading)
                    _item.Status = DownloadStatus.Paused;
                return;
            }

            if (process.ExitCode == 0)
                Complete(folder, baseName);
            else
                Fail(YtDlpService.DescribeError(stderr, process.ExitCode));
        }
        catch (OperationCanceledException)
        {
            if (_item.Status is DownloadStatus.Queued or DownloadStatus.Connecting or DownloadStatus.Downloading)
                _item.Status = DownloadStatus.Paused;
        }
        catch (YtDlpException)
        {
            throw;
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
                throw new YtDlpException("The video download failed: " + ex.Message, ex);
            }
        }
        finally
        {
            lock (_gate)
                _process = null;

            _item.BytesPerSecond = 0;
        }
    }

    public async Task PauseAsync()
    {
        Process? process;
        lock (_gate)
        {
            _paused = true;
            process = _process;
        }

        _cts.Cancel();

        if (process is not null)
        {
            YtDlpService.Kill(process);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The process is being killed; failure to wait is not important.
            }
        }

        _item.BytesPerSecond = 0;
        if (_item.Status is DownloadStatus.Queued or DownloadStatus.Connecting or DownloadStatus.Downloading)
            _item.Status = DownloadStatus.Paused;
    }

    public void SetSpeedLimit(long bytesPerSecond)
    {
        // yt-dlp applies the rate limit when it starts. A changed limit takes
        // effect the next time a paused video is resumed.
        _speedLimit = Math.Max(0, bytesPerSecond);
    }

    /// <summary>
    /// Removes yt-dlp's partial files. When <paramref name="deleteFinal"/> is
    /// false a completed media file is left on disk, but .part/.ytdl fragments
    /// are still cleaned up.
    /// </summary>
    public static void DeleteArtifacts(DownloadItem item, bool deleteFinal)
    {
        string folder = Path.GetDirectoryName(item.FilePath) ?? string.Empty;
        string baseName = Path.GetFileNameWithoutExtension(item.FilePath);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder) || string.IsNullOrWhiteSpace(baseName))
            return;

        try
        {
            foreach (string file in Directory.EnumerateFiles(folder, baseName + ".*"))
            {
                string name = Path.GetFileName(file);
                bool partial = name.Contains(".part", StringComparison.OrdinalIgnoreCase) ||
                               name.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase) ||
                               name.EndsWith(".temp", StringComparison.OrdinalIgnoreCase) ||
                               name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

                if (deleteFinal || partial)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception)
                    {
                        // A file held by a dying process can be cleaned up later.
                    }
                }
            }
        }
        catch (Exception)
        {
            // Enumerating a folder that is being removed is harmless.
        }

        if (deleteFinal)
        {
            try
            {
                if (File.Exists(item.FilePath))
                    File.Delete(item.FilePath);
            }
            catch (Exception)
            {
                // Ignore in-use files.
            }
        }
    }

    private ProcessStartInfo BuildStartInfo(string outputTemplate)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _service.YtDlpPath!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var arguments = startInfo.ArgumentList;
        arguments.Add("--no-playlist");
        arguments.Add("--newline");
        arguments.Add("--progress");
        arguments.Add("--no-warnings");
        arguments.Add("--no-colors");
        arguments.Add("--no-mtime");
        arguments.Add("--continue");
        arguments.Add("--retries");
        arguments.Add("10");
        arguments.Add("--fragment-retries");
        arguments.Add("10");
        arguments.Add("--socket-timeout");
        arguments.Add("30");

        if (!string.IsNullOrWhiteSpace(_service.FfmpegDirectory))
        {
            arguments.Add("--ffmpeg-location");
            arguments.Add(_service.FfmpegDirectory);
        }

        int fragments = Math.Clamp(_item.MaxConnections > 0 ? _item.MaxConnections : _settings.MaxConnections, 1, 8);
        arguments.Add("--concurrent-fragments");
        arguments.Add(fragments.ToString(CultureInfo.InvariantCulture));

        if (_speedLimit > 0)
        {
            arguments.Add("--limit-rate");
            arguments.Add(YtDlpService.FormatRate(_speedLimit));
        }

        if (_item.VideoConvertToMp3)
        {
            arguments.Add("--extract-audio");
            arguments.Add("--audio-format");
            arguments.Add("mp3");
            arguments.Add("--audio-quality");
            arguments.Add(_settings.AudioBitrateKbps > 0
                ? _settings.AudioBitrateKbps.ToString(CultureInfo.InvariantCulture)
                : "0");
        }

        if (_settings.DownloadSubtitles && !string.IsNullOrWhiteSpace(_settings.SubtitleLanguages))
        {
            arguments.Add("--write-subs");
            arguments.Add("--write-auto-subs");
            arguments.Add("--sub-langs");
            arguments.Add(_settings.SubtitleLanguages);
            arguments.Add("--embed-subs");
        }

        _service.AddProxyArgument(startInfo);
        _service.AddCookiesArgument(startInfo);

        if (!string.IsNullOrWhiteSpace(_item.VideoMergeContainer))
        {
            arguments.Add("--merge-output-format");
            arguments.Add(_item.VideoMergeContainer);
        }

        arguments.Add("--progress-template");
        arguments.Add("download:ADMPROG|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|" +
                        "%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s");

        arguments.Add("--print");
        arguments.Add("after_move:ADMFILE|%(filepath)s");

        arguments.Add("-f");
        arguments.Add(string.IsNullOrWhiteSpace(_item.VideoSelector) ? "bestvideo*+bestaudio/best" : _item.VideoSelector);

        arguments.Add("-o");
        arguments.Add(outputTemplate);

        arguments.Add(_item.Url);
        return startInfo;
    }

    private void HandleLine(string line)
    {
        if (YtDlpProgress.TryParse(line, out var progress))
        {
            if (_paused)
                return;

            _item.Status = DownloadStatus.Downloading;
            _item.BytesDownloaded = progress.DownloadedBytes;
            if (progress.TotalBytes > 0)
                _item.TotalBytes = progress.TotalBytes;
            _item.BytesPerSecond = progress.BytesPerSecond;
            return;
        }

        string trimmed = line.Trim();
        if (trimmed.StartsWith(YtDlpProgress.FilePrefix, StringComparison.Ordinal))
        {
            string path = trimmed[YtDlpProgress.FilePrefix.Length..].Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(path))
                _finalPath = path;
        }
    }

    private void Complete(string folder, string baseName)
    {
        string? finalPath = null;
        if (!string.IsNullOrWhiteSpace(_finalPath))
        {
            finalPath = Path.IsPathRooted(_finalPath)
                ? _finalPath
                : Path.Combine(folder, _finalPath);
        }

        if (string.IsNullOrWhiteSpace(finalPath) || !File.Exists(finalPath))
            finalPath = FindFinalFile(folder, baseName);

        if (string.IsNullOrWhiteSpace(finalPath) || !File.Exists(finalPath))
        {
            Fail("yt-dlp finished, but the output file could not be found.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(_item.ExpectedChecksum) &&
            _item.ChecksumAlgorithm != ChecksumAlgorithm.None)
        {
            try
            {
                string actual = Checksum.Compute(finalPath, _item.ChecksumAlgorithm);
                if (!string.Equals(actual, _item.ExpectedChecksum.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    Fail($"Checksum mismatch. Expected {_item.ExpectedChecksum.Trim()}, got {actual}.");
                    return;
                }
            }
            catch (Exception ex)
            {
                Fail("Could not verify the downloaded file checksum: " + ex.Message);
                return;
            }
        }

        try
        {
            long length = new FileInfo(finalPath).Length;
            _item.FilePath = finalPath;
            _item.TotalBytes = length;
            _item.BytesDownloaded = length;
        }
        catch (Exception)
        {
            _item.FilePath = finalPath;
        }

        _item.BytesPerSecond = 0;
        _item.CompletedAt = DateTimeOffset.Now;
        _item.Status = DownloadStatus.Completed;
    }

    private void Fail(string message)
    {
        _item.BytesPerSecond = 0;
        _item.ErrorMessage = string.IsNullOrWhiteSpace(message) ? "The video download failed." : message;
        _item.Status = DownloadStatus.Failed;
    }

    private static string? FindFinalFile(string folder, string baseName)
    {
        try
        {
            return Directory
                .EnumerateFiles(folder, baseName + ".*")
                .Where(file => !file.Contains(".part", StringComparison.OrdinalIgnoreCase))
                .Where(file => !file.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase))
                .Where(file => !file.EndsWith(".info.json", StringComparison.OrdinalIgnoreCase))
                .Where(file => !file.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase))
                .Where(file => !file.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
                .Where(file => !file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                .Where(file => !file.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                .Where(file => !file.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
