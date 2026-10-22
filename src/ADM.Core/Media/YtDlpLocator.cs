namespace Adm.Core.Media;

/// <summary>
/// Finds the bundled yt-dlp and ffmpeg executables. Published builds keep them
/// in <c>tools\</c> next to ADM.exe; source builds fall back to the repository's
/// <c>vendor\</c> folder and finally to PATH.
/// </summary>
public static class YtDlpLocator
{
    public const string YtDlpFileName = "yt-dlp.exe";
    public const string FfmpegFileName = "ffmpeg.exe";
    public const string FfprobeFileName = "ffprobe.exe";

    public static string? FindYtDlp() =>
        FindTool("ADM_YTDLP", YtDlpFileName);

    public static string? FindFfmpeg() =>
        FindTool("ADM_FFMPEG", FfmpegFileName);

    public static string? FindFfprobe() =>
        FindTool("ADM_FFPROBE", FfprobeFileName);

    private static string? FindTool(string environmentVariable, string fileName)
    {
        string? overridePath = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            overridePath = Environment.ExpandEnvironmentVariables(overridePath.Trim().Trim('"'));
            if (File.Exists(overridePath))
                return Path.GetFullPath(overridePath);

            // Also accept a directory in the override variable.
            if (Directory.Exists(overridePath))
            {
                string inDirectory = Path.Combine(overridePath, fileName);
                if (File.Exists(inDirectory))
                    return Path.GetFullPath(inDirectory);
            }
        }

        try
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            for (int depth = 0; depth < 9 && directory is not null; depth++, directory = directory.Parent)
            {
                foreach (string subdirectory in new[] { "tools", "vendor", string.Empty })
                {
                    string candidate = string.IsNullOrEmpty(subdirectory)
                        ? Path.Combine(directory.FullName, fileName)
                        : Path.Combine(directory.FullName, subdirectory, fileName);

                    if (File.Exists(candidate))
                        return Path.GetFullPath(candidate);
                }
            }
        }
        catch (Exception)
        {
            // Malformed paths must not prevent the app from starting.
        }

        return FindOnPath(fileName);
    }

    private static string? FindOnPath(string fileName)
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string rawDirectory in path.Split(Path.PathSeparator))
        {
            string directory = rawDirectory.Trim().Trim('"');
            if (string.IsNullOrEmpty(directory))
                continue;

            try
            {
                string candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            catch (Exception)
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }
}
