using System.Globalization;

namespace Adm.Core.Media;

/// <summary>One parsed progress line from yt-dlp's custom progress template.</summary>
public readonly record struct YtDlpProgressInfo(
    long DownloadedBytes,
    long TotalBytes,
    double BytesPerSecond,
    int? EtaSeconds);

/// <summary>
/// Parses the machine-readable progress lines emitted by
/// <c>--progress-template "download:ADMPROG|..."</c>.
/// </summary>
public static class YtDlpProgress
{
    public const string ProgressPrefix = "ADMPROG|";
    public const string FilePrefix = "ADMFILE|";

    public static bool TryParse(string? line, out YtDlpProgressInfo progress)
    {
        progress = default;
        if (line is null)
            return false;

        line = line.Trim();
        if (!line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
            return false;

        string[] parts = line[ProgressPrefix.Length..].Split('|');
        if (parts.Length < 5)
            return false;

        long downloaded = ParseLong(parts[0]);
        long total = ParseLong(parts[1]);
        long estimate = ParseLong(parts[2]);
        double speed = ParseDouble(parts[3]);
        double eta = ParseDouble(parts[4]);

        if (total <= 0)
            total = estimate;

        progress = new YtDlpProgressInfo(
            Math.Max(0, downloaded),
            Math.Max(0, total),
            Math.Max(0, speed),
            eta > 0 ? (int)Math.Min(int.MaxValue, eta) : null);

        return true;
    }

    private static long ParseLong(string value)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result))
            return result;

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            return (long)number;

        return 0;
    }

    private static double ParseDouble(string value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result)
            ? result
            : 0;
    }
}
