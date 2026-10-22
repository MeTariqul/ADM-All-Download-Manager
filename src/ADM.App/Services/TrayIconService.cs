using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Adm.App.Services;

/// <summary>
/// Windows notification-area icon built directly on the shell API, so the WPF
/// app does not need the Windows Forms stack. ADM keeps running in the tray
/// when the main window is closed, so downloads continue in the background and
/// the browser extension can still reach the app.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const int WmTrayIcon = 0x8000 + 1;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int WmNull = 0x0000;

    private const int NimAdd = 0x00000000;
    private const int NimModify = 0x00000001;
    private const int NimDelete = 0x00000002;
    private const int NimSetVersion = 0x00000004;

    private const int NifMessage = 0x00000001;
    private const int NifIcon = 0x00000002;
    private const int NifTip = 0x00000004;
    private const int NifInfo = 0x00000010;

    private const int NiifInfo = 0x00000001;

    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;

    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;

    private const int IdShow = 1001;
    private const int IdPause = 1002;
    private const int IdResume = 1003;
    private const int IdExit = 1004;
    private const int IdUpdates = 1005;

    private const int IconIndex = 1;
    private const int NotifyIconVersion4 = 4;

    private const int ImageIcon = 1;
    private const int LoadFromFile = 0x00000010;
    private const int DefaultSize = 0x00000040;

    private readonly Action _showWindow;
    private readonly Action _pauseAll;
    private readonly Action _resumeAll;
    private readonly Action _exit;
    private readonly Action? _checkUpdates;

    private HwndSource? _source;
    private IntPtr _ownerHandle;
    private IntPtr _icon = IntPtr.Zero;
    private bool _disposed;

    public TrayIconService(
        Window owner,
        Action showWindow,
        Action pauseAll,
        Action resumeAll,
        Action exit,
        Action? checkUpdates = null)
    {
        _showWindow = showWindow;
        _pauseAll = pauseAll;
        _resumeAll = resumeAll;
        _exit = exit;
        _checkUpdates = checkUpdates;

        // The main window owns the icon. Its handle stays valid while the
        // window is hidden, so the shell keeps sending the callbacks.
        _ownerHandle = new WindowInteropHelper(owner).EnsureHandle();
        _source = HwndSource.FromHwnd(_ownerHandle);
        _source?.AddHook(WndProc);

        _icon = LoadApplicationIcon();

        var data = BuildData(_ownerHandle);
        data.uFlags = NifMessage | NifIcon | NifTip;
        data.uCallbackMessage = WmTrayIcon;
        data.szTip = "ADM — All Download Manager";
        data.hIcon = _icon;

        Shell_NotifyIcon(NimAdd, ref data);

        // Version 4 sends the cursor position with the callback.
        var version = BuildData(_ownerHandle);
        version.uVersion = NotifyIconVersion4;
        version.uFlags = 0;
        Shell_NotifyIcon(NimSetVersion, ref version);
    }

    /// <summary>Updates the tooltip, for example with the current queue state.</summary>
    public void SetText(string text)
    {
        if (_disposed)
            return;

        // The shell rejects tooltips longer than 63 characters.
        string value = string.IsNullOrWhiteSpace(text) ? "ADM — All Download Manager" : text;
        if (value.Length > 63)
            value = value[..63];

        try
        {
            var data = BuildData(OwnerHandle);
            data.uFlags = NifTip;
            data.szTip = value;
            Shell_NotifyIcon(NimModify, ref data);
        }
        catch (Exception)
        {
            // A rejected tooltip must never break the app.
        }
    }

    /// <summary>Shows a short balloon tip next to the icon.</summary>
    public void ShowNotice(string title, string message)
    {
        if (_disposed)
            return;

        try
        {
            var data = BuildData(OwnerHandle);
            data.uFlags = NifInfo;
            data.szInfoTitle = title.Length > 63 ? title[..63] : title;
            data.szInfo = message.Length > 255 ? message[..255] : message;
            data.dwInfoFlags = NiifInfo;
            Shell_NotifyIcon(NimModify, ref data);
        }
        catch (Exception)
        {
            // Balloons are a courtesy, not a requirement.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            var data = BuildData(OwnerHandle);
            Shell_NotifyIcon(NimDelete, ref data);
        }
        catch (Exception)
        {
            // The shell may already be gone during shutdown.
        }

        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }

        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private IntPtr OwnerHandle => _ownerHandle;

    private NotifyIconData BuildData(IntPtr owner) => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = owner,
        uID = IconIndex,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmTrayIcon)
        {
            // With NOTIFYICON_VERSION_4 the event is in the low word of
            // lParam and the cursor position in the high word.
            int eventCode = (int)(long)lParam & 0xFFFF;
            int cursorX = (int)(long)lParam >> 16;
            int cursorY = (int)(long)wParam;

            switch (eventCode)
            {
                case WmLButtonUp:
                case WmLButtonDblClk:
                    _showWindow();
                    handled = true;
                    return IntPtr.Zero;

                case WmRButtonUp:
                    ShowContextMenu(hwnd, cursorX, cursorY);
                    handled = true;
                    return IntPtr.Zero;
            }
        }

        return IntPtr.Zero;
    }

    private void ShowContextMenu(IntPtr owner, int cursorX, int cursorY)
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;

        try
        {
            AppendMenu(menu, MfString, IdShow, "Show ADM");
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, IdPause, "Pause all downloads");
            AppendMenu(menu, MfString, IdResume, "Resume all downloads");
            AppendMenu(menu, MfSeparator, 0, null);

            if (_checkUpdates is not null)
                AppendMenu(menu, MfString, IdUpdates, "Check for updates");

            AppendMenu(menu, MfString, IdExit, "Exit ADM");

            // The menu has to be foreground for it to close when the user clicks away.
            SetForegroundWindow(owner);
            int command = TrackPopupMenu(menu, TpmRightButton | TpmReturnCmd, cursorX, cursorY, 0, owner, IntPtr.Zero);
            PostMessage(owner, WmNull, IntPtr.Zero, IntPtr.Zero);

            switch (command)
            {
                case IdShow:
                    _showWindow();
                    break;
                case IdPause:
                    _pauseAll();
                    break;
                case IdResume:
                    _resumeAll();
                    break;
                case IdExit:
                    _exit();
                    break;

                case IdUpdates:
                    _checkUpdates?.Invoke();
                    break;
            }
        }
        catch (Exception)
        {
            // A failed menu must never break the app.
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    /// <summary>
    /// Builds the notification-area icon from the embedded application icon.
    /// The file is written to a temporary path because the shell's icon loader
    /// only reads icons from disk; the PNG-compressed frames it contains are
    /// supported on Windows Vista and later.
    /// </summary>
    private static IntPtr LoadApplicationIcon()
    {
        string? temporaryFile = null;

        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
            if (info is not null)
            {
                temporaryFile = Path.Combine(Path.GetTempPath(), $"adm-tray-{Guid.NewGuid():N}.ico");
                using (var file = File.Create(temporaryFile))
                {
                    info.Stream.CopyTo(file);
                }

                IntPtr icon = LoadImage(IntPtr.Zero, temporaryFile, ImageIcon, 0, 0, LoadFromFile | DefaultSize);
                if (icon != IntPtr.Zero)
                    return icon;
            }
        }
        catch (Exception)
        {
            // Fall through to the executable icon.
        }
        finally
        {
            if (temporaryFile is not null)
            {
                try
                {
                    File.Delete(temporaryFile);
                }
                catch (Exception)
                {
                    // The temp file will be cleaned up by the system.
                }
            }
        }

        return LoadIconFromExecutable();
    }

    private static IntPtr LoadIconFromExecutable()
    {
        try
        {
            string? executable = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(executable) &&
                ExtractIconEx(executable, 0, out IntPtr large, out IntPtr small, 1) > 0)
            {
                if (large != IntPtr.Zero)
                    return large;

                if (small != IntPtr.Zero)
                    return small;
            }
        }
        catch (Exception)
        {
            // Fall through to the default icon.
        }

        return LoadIcon(IntPtr.Zero, 32512 /* IDI_APPLICATION */);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;

        // The shell posts this message to the owner window on a click.
        public int uCallbackMessage;

        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public int dwState;
        public int dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public int uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, int type, int cx, int cy, int load);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, int id);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string? text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr owner, IntPtr rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
