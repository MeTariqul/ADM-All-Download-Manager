using System.Globalization;
using System.Windows;
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
            "all" => "◈",
            "active" => "↗",
            "completed" => "✓",
            "paused" => "Ⅱ",
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
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is DownloadStatus status
            ? status switch
            {
                DownloadStatus.Completed => ThemeBrush("SuccessBrush", "#168568"),
                DownloadStatus.Failed => ThemeBrush("DangerBrush", "#C64F5A"),
                DownloadStatus.Paused => ThemeBrush("TextMutedBrush", "#849892"),
                DownloadStatus.Queued => ThemeBrush("TextMutedBrush", "#849892"),
                _ => ThemeBrush("AccentBrush", "#087EA4")
            }
            : ThemeBrush("AccentBrush", "#087EA4");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Brush ThemeBrush(string key, string fallback)
    {
        var brush = Application.Current?.TryFindResource(key) as Brush ??
                    new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));
        if (brush.IsFrozen)
            return brush;

        if (brush is SolidColorBrush solidBrush && !solidBrush.IsFrozen)
        {
            solidBrush = solidBrush.Clone();
            solidBrush.Freeze();
            return solidBrush;
        }

        return brush;
    }
}
