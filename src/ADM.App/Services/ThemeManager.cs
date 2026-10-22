using System.Windows;
using Adm.Core.Util;

namespace Adm.App.Services;

/// <summary>
/// Swaps the colour palette at runtime. The styles live in a resource
/// dictionary; the dark palette simply overrides the brush keys.
/// </summary>
public static class ThemeManager
{
    private const string DarkSource = "pack://application:,,,/Themes/Dark.xaml";

    /// <summary>Applies the light or dark palette to the running application.</summary>
    public static void Apply(bool dark)
    {
        try
        {
            var app = Application.Current;
            if (app is null)
                return;

            var resources = app.Resources;
            var merged = resources.MergedDictionaries;

            // The light palette is the first merged dictionary; the dark one is
            // added on top of it so the brushes override cleanly.
            for (int i = merged.Count - 1; i >= 0; i--)
            {
                if (merged[i].Source?.OriginalString?.Contains("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase) == true)
                    merged.RemoveAt(i);
            }

            if (!dark)
                return;

            var dictionary = new ResourceDictionary
            {
                Source = new Uri(DarkSource, UriKind.Absolute)
            };

            merged.Add(dictionary);

            // A missing brush would leave the window unreadable, so the
            // resolved colours are written to the log.
            var background = app.TryFindResource("WindowBackgroundBrush") as System.Windows.Media.SolidColorBrush;
            DiagnosticsLog.Info($"The dark theme was applied (background {background?.Color}).");
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Warn("Could not switch the theme.", ex);
        }
    }
}
