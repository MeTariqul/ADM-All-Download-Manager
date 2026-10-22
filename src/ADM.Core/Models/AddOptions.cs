using Adm.Core.Util;

namespace Adm.Core.Models;

/// <summary>Optional overrides when a download is created.</summary>
public sealed class AddOptions
{
    public string? Folder { get; set; }

    public string? FileName { get; set; }

    public string? Referrer { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>Cookie header forwarded from the browser.</summary>
    public string? Cookie { get; set; }

    /// <summary>Value for the Authorization header.</summary>
    public string? Authorization { get; set; }

    public int? Connections { get; set; }

    public long? SpeedLimitBytesPerSecond { get; set; }

    /// <summary>Higher values start the download earlier in the queue.</summary>
    public int? Priority { get; set; }

    /// <summary>Expected hash of the finished file.</summary>
    public string? ExpectedChecksum { get; set; }

    /// <summary>Algorithm of <see cref="ExpectedChecksum"/>.</summary>
    public ChecksumAlgorithm ChecksumAlgorithm { get; set; } = ChecksumAlgorithm.None;
}
