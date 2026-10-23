using System.Text;

namespace Adm.Core.Util;

/// <summary>
/// A small rolling log written next to the settings file. It gives users
/// something to send when a download misbehaves, and it never throws: a broken
/// log must not break a download.
/// </summary>
public static class DiagnosticsLog
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private const string BackupSuffix = ".1";
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

    /// <summary>Returns a URL origin suitable for logs, without credentials, path, query, or fragment.</summary>
    public static string SafeEndpoint(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "ftp" or "ftps"))
            return "[invalid URL]";

        string host = uri.HostNameType == UriHostNameType.IPv6 ? $"[{uri.IdnHost}]" : uri.IdnHost;
        int defaultPort = uri.Scheme switch
        {
            "http" or "ftp" => 80,
            "https" => 443,
            "ftps" => 990,
            _ => -1
        };

        return uri.IsDefaultPort || uri.Port == defaultPort
            ? $"{uri.Scheme}://{host}"
            : $"{uri.Scheme}://{host}:{uri.Port}";
    }

    private static void Write(string level, string message, Exception? error)
    {
        try
        {
            lock (Gate)
            {
                if (!_enabled || string.IsNullOrEmpty(_path))
                    return;

                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);

                var builder = new StringBuilder();
                builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                builder.Append(" [").Append(level).Append("] ");
                builder.Append(message);

                if (error is not null)
                {
                    builder.Append(" | ").Append(error.GetType().Name).Append(": ").Append(error.Message);
                }

                builder.AppendLine();
                string entry = builder.ToString();
                if (File.Exists(_path) && new FileInfo(_path).Length + Encoding.UTF8.GetByteCount(entry) > MaxBytes)
                {
                    string backup = _path + BackupSuffix;
                    if (File.Exists(backup))
                        File.Delete(backup);
                    File.Move(_path, backup);
                }

                File.AppendAllText(_path, entry);
            }
        }
        catch (Exception)
        {
            // Logging is best-effort.
        }
    }
}
