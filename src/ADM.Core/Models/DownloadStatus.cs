namespace Adm.Core.Models;

/// <summary>Lifecycle of a single download.</summary>
public enum DownloadStatus
{
    Queued,
    Connecting,
    Downloading,
    Paused,
    Completed,
    Failed
}
