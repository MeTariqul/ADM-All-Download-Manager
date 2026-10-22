namespace Adm.Core.Engine;

/// <summary>
/// Common lifecycle used by the download queue. Both the segmented HTTP engine
/// and the external yt-dlp engine implement it so the manager can treat them
/// the same way.
/// </summary>
public interface IDownloadTask
{
    Task StartAsync();

    Task PauseAsync();

    void SetSpeedLimit(long bytesPerSecond);
}
