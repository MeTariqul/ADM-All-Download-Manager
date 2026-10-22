namespace Adm.Core.Media;

/// <summary>A user-facing failure from the yt-dlp engine.</summary>
public sealed class YtDlpException : Exception
{
    public YtDlpException(string message) : base(message)
    {
    }

    public YtDlpException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
