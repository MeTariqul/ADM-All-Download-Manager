using System.Text;

namespace Adm.Core.Util;

/// <summary>
/// A small append-only log written next to the settings file. It gives users
/// something to send when a download misbehaves, and it never throws: a broken
/// log must not break a download.
/// </summary>
public static class DiagnosticsLog
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();
    private static bool _enabled = true;
    private static string? _path;

    /// <summary>Enables or disables writing. Disabled logging still allows reading.</summary>
    public static void Configure(string? path, bool enabled)
    {
        lock (Gate)
        {
            _path = path;
            _enabled = enabled;
        }
    }

    public static string? Path
    {
        get
        {
            lock (Gate)
                return _path;
        }
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message, Exception? error = null) => Write("WARN", message, error);

    public static void Error(string message, Exception? error = null) => Write("ERROR", message, error);

    private static void Write(string level, string message, Exception? error)
    {
        try
        {
            lock (Gate)
            {
                if (!_enabled || string.IsNullOrEmpty(_path))
                    return;

                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);

                if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
                    File.Delete(_path);

                var builder = new StringBuilder();
                builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                builder.Append(" [").Append(level).Append("] ");
                builder.Append(message);

                if (error is not null)
                {
                    builder.Append(" | ").Append(error.GetType().Name).Append(": ").Append(error.Message);
                }

                builder.AppendLine();
                File.AppendAllText(_path, builder.ToString());
            }
        }
        catch (Exception)
        {
            // Logging is best-effort.
        }
    }
}
