namespace Adm.Core.Models;

/// <summary>Which engine owns a download.</summary>
public enum DownloadKind
{
    /// <summary>Segmented HTTP/HTTPS transfer.</summary>
    Http = 0,

    /// <summary>External yt-dlp process (video and audio sites).</summary>
    Video = 1,

    /// <summary>FTP or FTPS transfer.</summary>
    Ftp = 2
}
