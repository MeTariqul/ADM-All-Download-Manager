using System.Globalization;

namespace Adm.Core.Util;

/// <summary>Human friendly formatting helpers shared by the UI and logs.</summary>
public static class Format
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public static string Bytes(long bytes)
    {
        if (bytes <= 0)
            return "0 B";

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        string format = value >= 100 || unit == 0 ? "0" : value >= 10 ? "0.0" : "0.00";
        return value.ToString(format, CultureInfo.InvariantCulture) + " " + Units[unit];
    }

    public static string Speed(double bytesPerSecond)
    {
        if (bytesPerSecond < 1)
            return string.Empty;

        return Bytes((long)bytesPerSecond) + "/s";
    }

    public static string TimeLeft(TimeSpan? remaining)
    {
        if (remaining is null || remaining.Value.TotalSeconds < 1)
            return string.Empty;

        var value = remaining.Value;
        if (value.TotalHours >= 1)
            return $"{(int)value.TotalHours}h {value.Minutes}m left";

        if (value.TotalMinutes >= 1)
            return $"{value.Minutes}m {value.Seconds}s left";

        return $"{Math.Max(1, value.Seconds)}s left";
    }
}
