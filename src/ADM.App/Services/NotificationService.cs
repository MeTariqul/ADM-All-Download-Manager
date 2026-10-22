using Adm.Core.Util;

namespace Adm.App.Services;

/// <summary>
/// Shows a Windows notification when a download finishes. The notification is
/// drawn by the shell next to the notification-area icon, so it also appears in
/// the notification centre on Windows 11.
/// </summary>
public sealed class NotificationService
{
    private readonly TrayIconService? _tray;

    public NotificationService(TrayIconService? tray)
    {
        _tray = tray;
    }

    /// <summary>Reports a finished or failed download.</summary>
    public void NotifyDownloadFinished(string fileName, bool succeeded, string? error = null)
    {
        string title = succeeded ? "Download finished" : "Download failed";
        string message = succeeded
            ? fileName
            : string.IsNullOrWhiteSpace(error) ? fileName : $"{fileName} — {error}";

        Notify(title, message);
    }

    /// <summary>Shows a general notification, for example a clipboard link offer.</summary>
    public void Notify(string title, string message)
    {
        try
        {
            _tray?.ShowNotice(title, message);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Warn("Could not show the notification.", ex);
        }
    }
}
