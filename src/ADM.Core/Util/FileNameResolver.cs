using System.Text;

namespace Adm.Core.Util;

/// <summary>Turns URLs and Content-Disposition headers into safe, unique file names.</summary>
public static class FileNameResolver
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string FromUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var name = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
            if (!string.IsNullOrWhiteSpace(name))
                return Sanitize(name);
        }

        return "download.bin";
    }

    /// <summary>Prefers RFC 5987 <c>filename*</c>, then <c>filename</c>, then the final URL.</summary>
    public static string Resolve(string? contentDispositionFileName, string? contentDispositionFileNameStar,
        string? finalUrl, string? contentType)
    {
        var name = contentDispositionFileNameStar;
        if (string.IsNullOrWhiteSpace(name))
            name = contentDispositionFileName;

        if (!string.IsNullOrWhiteSpace(name))
        {
            name = Unquote(name);
            // Some servers escape the value twice; decode once more when it helps.
            if (name.Contains('%'))
            {
                try { name = Uri.UnescapeDataString(name); }
                catch (UriFormatException) { /* keep the raw value */ }
            }
        }

        if (string.IsNullOrWhiteSpace(name) || name is "\"\"" or "''")
            name = FromUrl(finalUrl ?? string.Empty);

        name = Sanitize(name);

        if (string.IsNullOrEmpty(Path.GetExtension(name)))
        {
            var extension = CategoryDetector.ExtensionFromContentType(contentType);
            if (extension is not null)
                name += extension;
        }

        return name;
    }

    public static string Sanitize(string name)
    {
        name = name.Trim().Trim('"', '\'');

        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (ch < 32 || Path.GetInvalidFileNameChars().Contains(ch))
                builder.Append('_');
            else
                builder.Append(ch);
        }

        var safe = builder.ToString().Trim();
        if (string.IsNullOrWhiteSpace(safe) || safe == ".")
            safe = "download.bin";

        var stem = Path.GetFileNameWithoutExtension(safe);
        if (ReservedNames.Contains(stem))
            safe = "_" + safe;

        if (safe.Length > 180)
        {
            var extension = Path.GetExtension(safe);
            safe = safe[..(180 - extension.Length)] + extension;
        }

        return safe.TrimEnd('.', ' ');
    }

    /// <summary>Returns a path that does not collide with an existing file or partial download.</summary>
    public static string UniquePath(string folder, string fileName)
    {
        var candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate) && !File.Exists(candidate + ".admpart"))
            return candidate;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (int counter = 2; counter < 10_000; counter++)
        {
            candidate = Path.Combine(folder, $"{stem} ({counter}){extension}");
            if (!File.Exists(candidate) && !File.Exists(candidate + ".admpart"))
                return candidate;
        }

        return Path.Combine(folder, $"{stem} ({Guid.NewGuid():N}){extension}");
    }

    private static string Unquote(string value) => value.Trim().Trim('"');
}
