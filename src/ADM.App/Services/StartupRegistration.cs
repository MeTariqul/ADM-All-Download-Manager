using Microsoft.Win32;

namespace Adm.App.Services;

/// <summary>Adds or removes the per-user Run entry used by "Start with Windows".</summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ADM";

    /// <summary>The argument that makes a Windows-started ADM open in the tray.</summary>
    public const string MinimizedArgument = "--minimized";

    public static void Apply(bool enabled, bool startInTray)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
                return;

            if (enabled)
            {
                var executable = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(executable))
                    key.SetValue(ValueName, BuildCommand(executable, startInTray));
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception)
        {
            // A locked-down registry should not prevent saving other settings.
        }
    }

    /// <summary>
    /// Rewrites an existing Run entry so entries written by the installer or
    /// older versions pick up the tray flag without the user changing anything.
    /// </summary>
    public static void RefreshExisting(bool startInTray)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
                return;

            var current = key.GetValue(ValueName) as string;
            var executable = Environment.ProcessPath;

            // Only touch entries that already launch ADM itself.
            if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(executable) ||
                !current.Contains($"\"{executable}\"", StringComparison.OrdinalIgnoreCase))
                return;

            key.SetValue(ValueName, BuildCommand(executable, startInTray));
        }
        catch (Exception)
        {
            // A locked-down registry should not prevent the app from starting.
        }
    }

    private static string BuildCommand(string executable, bool startInTray) =>
        startInTray ? $"\"{executable}\" {MinimizedArgument}" : $"\"{executable}\"";
}
