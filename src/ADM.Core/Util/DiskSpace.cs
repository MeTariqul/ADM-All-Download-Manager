namespace Adm.Core.Util;

/// <summary>Free-space information for the volume that will hold a download.</summary>
public sealed record DiskSpaceInfo(long FreeBytes, long TotalBytes)
{
    public bool IsKnown => FreeBytes >= 0;

    public string FreeText => IsKnown ? Format.Bytes(FreeBytes) : "unknown";
}

/// <summary>Checks that the target volume can still hold a download before it starts.</summary>
public static class DiskSpace
{
    /// <summary>
    /// Returns the free space of the volume that contains <paramref name="path"/>.
    /// An unknown volume reports <c>-1</c> rather than failing the download.
    /// </summary>
    public static DiskSpaceInfo ForPath(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root))
                return new DiskSpaceInfo(-1, -1);

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                return new DiskSpaceInfo(-1, -1);

            return new DiskSpaceInfo(drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception)
        {
            // Unknown volumes (network shares that are offline, for example)
            // must not stop the download.
            return new DiskSpaceInfo(-1, -1);
        }
    }

    /// <summary>
    /// Returns a warning when the volume does not have enough room, or null
    /// when the download fits. A small margin keeps a running download from
    /// filling the disk completely.
    /// </summary>
    public static string? Check(string folder, long requiredBytes)
    {
        if (requiredBytes <= 0)
            return null;

        var info = ForPath(folder);
        if (!info.IsKnown)
            return null;

        // Leave a little headroom so the system stays usable.
        const long Margin = 64L * 1024 * 1024;
        if (info.FreeBytes >= requiredBytes + Margin)
            return null;

        return $"Not enough free space on the target drive. " +
               $"The download needs {Format.Bytes(requiredBytes)} but only {info.FreeText} is available.";
    }
}
