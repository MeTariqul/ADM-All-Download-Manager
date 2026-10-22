using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Adm.Core.Media;

/// <summary>
/// Thin wrapper around the bundled yt-dlp executable: probes a page for its
/// available formats and exposes the ffmpeg location used by downloads.
/// </summary>
public sealed class YtDlpService
{
    private static readonly Regex AnsiPattern = new(
        @"\x1B\[[0-9;]*[A-Za-z]",
        RegexOptions.Compiled);

    public YtDlpService(string? ytDlpPath = null, string? ffmpegPath = null)
    {
        YtDlpPath = string.IsNullOrWhiteSpace(ytDlpPath) ? YtDlpLocator.FindYtDlp() : ytDlpPath;
        FfmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? YtDlpLocator.FindFfmpeg() : ffmpegPath;
        FfprobePath = string.IsNullOrWhiteSpace(FfmpegPath)
            ? YtDlpLocator.FindFfprobe()
            : Path.Combine(Path.GetDirectoryName(FfmpegPath) ?? string.Empty, YtDlpLocator.FfprobeFileName);

        if (!string.IsNullOrWhiteSpace(FfprobePath) && !File.Exists(FfprobePath))
            FfprobePath = YtDlpLocator.FindFfprobe();
    }

    public string? YtDlpPath { get; }

    public string? FfmpegPath { get; }

    public string? FfprobePath { get; }

    public bool IsAvailable => !string.IsNullOrWhiteSpace(YtDlpPath) && File.Exists(YtDlpPath);

    public bool HasFfmpeg => !string.IsNullOrWhiteSpace(FfmpegPath) && File.Exists(FfmpegPath);

    public string? FfmpegDirectory =>
        HasFfmpeg ? Path.GetDirectoryName(FfmpegPath) : null;

    /// <summary>Proxy applied to the yt-dlp process, if the user configured one.</summary>
    public string? ProxyUrl { get; set; }

    /// <summary>Browser whose cookies yt-dlp may read.</summary>
    public string? CookiesFromBrowser { get; set; }

    /// <summary>Read the version of the bundled yt-dlp, or an empty string.</summary>
    public async Task<string> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            return string.Empty;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = YtDlpPath!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            startInfo.ArgumentList.Add("--version");

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return string.Empty;

            string output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return output.Trim();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Updates the bundled yt-dlp in place. The tool replaces its own file, so
    /// it must own the file it writes; the running process is started from a
    /// temporary copy which is then swapped in.
    /// </summary>
    public async Task<(bool Success, string Message)> UpdateAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            return (false, "yt-dlp was not found, so it cannot be updated.");

        string path = YtDlpPath!;
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
            return (false, "The yt-dlp location is not writable.");

        string temporary = Path.Combine(directory, $"yt-dlp-{Guid.NewGuid():N}.exe");

        try
        {
            File.Copy(path, temporary, overwrite: true);

            var startInfo = new ProcessStartInfo
            {
                FileName = temporary,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            startInfo.ArgumentList.Add("--update-to");
            startInfo.ArgumentList.Add("stable");
            startInfo.ArgumentList.Add("--no-colors");

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return (false, "yt-dlp could not be started.");

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            string output = await outputTask.ConfigureAwait(false);
            string error = await errorTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                string detail = DescribeError(error, process.ExitCode);
                return (false, "yt-dlp could not update itself: " + detail);
            }

            // The updated copy is the one that was run; put it in place.
            File.Copy(temporary, path, overwrite: true);

            return (true, string.IsNullOrWhiteSpace(output) ? "yt-dlp was updated." : output.Trim());
        }
        catch (OperationCanceledException)
        {
            return (false, "The update was cancelled.");
        }
        catch (Exception ex)
        {
            return (false, "yt-dlp could not be updated: " + ex.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch (Exception)
            {
                // The temporary file is harmless.
            }
        }
    }

    /// <summary>Lists the videos of a playlist without downloading them.</summary>
    public async Task<PlaylistInfo> GetPlaylistAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            throw new YtDlpException(
                "The video engine (yt-dlp) is missing. Reinstall ADM or run tools\\Get-VideoTools.ps1, then try again.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = YtDlpPath!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add("-J");
        startInfo.ArgumentList.Add("--flat-playlist");
        startInfo.ArgumentList.Add("--skip-download");
        startInfo.ArgumentList.Add("--no-progress");
        startInfo.ArgumentList.Add("--no-colors");
        startInfo.ArgumentList.Add("--socket-timeout");
        startInfo.ArgumentList.Add("20");

        AddProxyArgument(startInfo);
        AddCookiesArgument(startInfo);

        startInfo.ArgumentList.Add(url);

        using var process = new Process { StartInfo = startInfo };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));

        try
        {
            if (!process.Start())
                throw new YtDlpException("The video engine could not be started.");

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
                throw new YtDlpException(DescribeError(stderr, process.ExitCode));

            return YtDlpPlaylistParser.Parse(stdout, url);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new YtDlpException("Reading the playlist timed out. Check your connection and try again.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (YtDlpException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new YtDlpException("Could not read the playlist: " + ex.Message, ex);
        }
    }

    /// <summary>Adds the proxy argument when one is configured.</summary>
    public void AddProxyArgument(ProcessStartInfo startInfo)
    {
        if (!string.IsNullOrWhiteSpace(ProxyUrl))
        {
            startInfo.ArgumentList.Add("--proxy");
            startInfo.ArgumentList.Add(ProxyUrl);
        }
    }

    /// <summary>Adds the browser cookie argument when one is configured.</summary>
    public void AddCookiesArgument(ProcessStartInfo startInfo)
    {
        if (!string.IsNullOrWhiteSpace(CookiesFromBrowser))
        {
            startInfo.ArgumentList.Add("--cookies-from-browser");
            startInfo.ArgumentList.Add(CookiesFromBrowser);
        }
    }

    public async Task<VideoInfo> GetInfoAsync(string url, CancellationToken cancellationToken = default,
        bool allowPlaylist = false)
    {
        if (!IsAvailable)
        {
            throw new YtDlpException(
                "The video engine (yt-dlp) is missing. Reinstall ADM or run tools\\Get-VideoTools.ps1, then try again.");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new YtDlpException("Only http:// and https:// video links are supported.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = YtDlpPath!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        AddCommonArguments(startInfo, FfmpegDirectory);
        AddProxyArgument(startInfo);
        AddCookiesArgument(startInfo);

        startInfo.ArgumentList.Add("-J");
        startInfo.ArgumentList.Add("--skip-download");
        startInfo.ArgumentList.Add("--no-progress");
        startInfo.ArgumentList.Add("--socket-timeout");
        startInfo.ArgumentList.Add("20");
        startInfo.ArgumentList.Add(url);

        using var process = new Process { StartInfo = startInfo };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));

        try
        {
            if (!process.Start())
                throw new YtDlpException("The video engine could not be started.");

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                throw;
            }

            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
                throw new YtDlpException(DescribeError(stderr, process.ExitCode));

            return YtDlpFormatParser.Parse(stdout, url, allowPlaylist);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new YtDlpException("Reading video information timed out. Check your connection and try again.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (YtDlpException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new YtDlpException("Could not read video information: " + ex.Message, ex);
        }
    }

    internal static void AddCommonArguments(ProcessStartInfo startInfo, string? ffmpegDirectory)
    {
        startInfo.ArgumentList.Add("--no-playlist");
        startInfo.ArgumentList.Add("--no-warnings");
        startInfo.ArgumentList.Add("--no-colors");
        startInfo.ArgumentList.Add("--no-mtime");

        if (!string.IsNullOrWhiteSpace(ffmpegDirectory))
        {
            startInfo.ArgumentList.Add("--ffmpeg-location");
            startInfo.ArgumentList.Add(ffmpegDirectory);
        }
    }

    internal static string DescribeError(string? stderr, int exitCode)
    {
        string text = AnsiPattern.Replace(stderr ?? string.Empty, string.Empty);
        var errors = text
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Contains("ERROR:", StringComparison.OrdinalIgnoreCase))
            .Select(line => Regex.Replace(line, @"^ERROR:\s*", string.Empty, RegexOptions.IgnoreCase))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        if (errors.Count == 0)
        {
            var lastLines = text
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .TakeLast(2)
                .ToList();

            string fallback = string.Join(" ", lastLines);
            return string.IsNullOrWhiteSpace(fallback)
                ? $"yt-dlp exited with code {exitCode}."
                : fallback;
        }

        string message = string.Join(" ", errors.TakeLast(2));
        return message.Length <= 500 ? message : message[..500].TrimEnd() + "…";
    }

    internal static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // The process may already have exited.
        }
    }

    internal static string FormatRate(long bytesPerSecond) =>
        bytesPerSecond.ToString(CultureInfo.InvariantCulture);
}
