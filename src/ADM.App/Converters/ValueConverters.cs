using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Adm.Core.Models;

namespace Adm.App.Converters;

/// <summary>Maps a download category to a compact glyph for the file badge.</summary>
public sealed class CategoryGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return (value as string)?.ToLowerInvariant() switch
        {
            "video" => "▶",
            "music" => "♪",
            "documents" => "▤",
            "programs" => "⚙",
            "archives" => "▦",
            "images" => "▧",
            _ => "↓"
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Colors progress bars and status accents based on download state.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    private static readonly Brush Accent = Frozen("#111318");
    private static readonly Brush Success = Frozen("#16A34A");
    private static readonly Brush Danger = Frozen("#DC2626");
    private static readonly Brush Muted = Frozen("#9AA0AA");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is DownloadStatus status
            ? status switch
            {
                DownloadStatus.Completed => Success,
                DownloadStatus.Failed => Danger,
                DownloadStatus.Paused => Muted,
                DownloadStatus.Queued => Muted,
                _ => Accent
            }
            : Accent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
