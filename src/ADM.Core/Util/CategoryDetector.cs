namespace Adm.Core.Util;

/// <summary>Maps file extensions to friendly categories used by the sidebar.</summary>
public static class CategoryDetector
{
    private static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".ts", ".3gp"
    };

    private static readonly HashSet<string> Music = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".aac", ".flac", ".wav", ".ogg", ".opus", ".wma", ".aiff"
    };

    private static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".epub", ".mobi", ".csv", ".rtf", ".odt"
    };

    private static readonly HashSet<string> Programs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".appx", ".dmg", ".pkg", ".deb", ".rpm", ".apk", ".jar", ".iso"
    };

    private static readonly HashSet<string> Archives = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".zst"
    };

    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".svg", ".tiff", ".heic"
    };

    public static string Detect(string fileNameOrExtension)
    {
        var extension = fileNameOrExtension.StartsWith('.')
            ? fileNameOrExtension
            : Path.GetExtension(fileNameOrExtension);

        if (string.IsNullOrEmpty(extension))
            return "Other";

        if (Video.Contains(extension)) return "Video";
        if (Music.Contains(extension)) return "Music";
        if (Documents.Contains(extension)) return "Documents";
        if (Programs.Contains(extension)) return "Programs";
        if (Archives.Contains(extension)) return "Archives";
        if (Images.Contains(extension)) return "Images";
        return "Other";
    }

    /// <summary>Guesses an extension from a MIME type when the URL has none.</summary>
    public static string? ExtensionFromContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return null;

        var mime = contentType.Split(';')[0].Trim().ToLowerInvariant();
        return mime switch
        {
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            "video/x-matroska" => ".mkv",
            "audio/mpeg" => ".mp3",
            "audio/mp4" => ".m4a",
            "audio/aac" => ".aac",
            "audio/ogg" => ".ogg",
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "application/pdf" => ".pdf",
            "application/zip" => ".zip",
            "application/x-7z-compressed" => ".7z",
            "application/x-msdownload" => ".exe",
            "application/vnd.android.package-archive" => ".apk",
            "application/octet-stream" => ".bin",
            _ => null
        };
    }
}
