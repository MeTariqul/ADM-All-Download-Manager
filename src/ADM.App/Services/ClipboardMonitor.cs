using System.Windows;
using System.Windows.Threading;
using Adm.Core.Util;

namespace Adm.App.Services;

/// <summary>
/// Watches the clipboard for links and offers to download them. This is the
/// feature users expect from a download manager: copy a link anywhere and ADM
/// notices it.
/// </summary>
public sealed class ClipboardMonitor : IDisposable
{
    private const int PollIntervalMs = 900;

    private readonly DispatcherTimer _timer;
    private string _lastText = string.Empty;
    private bool _disposed;

    public ClipboardMonitor()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(PollIntervalMs)
        };

        _timer.Tick += (_, _) => Check();
    }

    /// <summary>Raised when a new link is found on the clipboard.</summary>
    public event EventHandler<string>? LinkFound;

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    private void Check()
    {
        try
        {
            if (!Clipboard.ContainsText())
                return;

            string text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text) || text == _lastText)
                return;

            _lastText = text;

            // Ignore anything that is clearly not a link, and text that is far
            // too long to be one.
            if (text.Length > 2048 || text.Contains('\n') || text.Contains('\r'))
                return;

            string? url = ExtractUrl(text);
            if (url is not null)
            {
                DiagnosticsLog.Info($"Clipboard link detected: {url}");
                LinkFound?.Invoke(this, url);
            }
        }
        catch (Exception)
        {
            // The clipboard can be locked by another process; try again later.
        }
    }

    /// <summary>Returns the first http or https link in the text, if there is one.</summary>
    internal static string? ExtractUrl(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        int index = trimmed.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            index = trimmed.IndexOf("http://", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return null;

        int end = index;
        while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end]) && trimmed[end] != '"' && trimmed[end] != '\'')
            end++;

        return trimmed[index..end].TrimEnd('.', ',', ';', ')', ']', '}');
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _timer.Stop();
    }
}
