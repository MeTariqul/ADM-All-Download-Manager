namespace Adm.Core.Models;

/// <summary>What to do when the same URL is queued twice.</summary>
public enum DuplicatePolicy
{
    /// <summary>Always queue a second copy.</summary>
    Allow = 0,

    /// <summary>Ignore the new request and keep the existing download.</summary>
    Skip = 1,

    /// <summary>Resume the existing download instead of queueing a new one.</summary>
    Resume = 2
}

/// <summary>What to do when the target file already exists on disk.</summary>
public enum FileExistsPolicy
{
    /// <summary>Add a numeric suffix, for example "movie (2).mp4".</summary>
    Rename = 0,

    /// <summary>Replace the existing file.</summary>
    Overwrite = 1,

    /// <summary>Leave the existing file alone and mark the download as done.</summary>
    Skip = 2
}

/// <summary>How a download reacts to a network or server error.</summary>
public enum RetryPolicy
{
    /// <summary>Give up as soon as the transfer fails.</summary>
    None = 0,

    /// <summary>Retry a few times, then give up.</summary>
    Limited = 1,

    /// <summary>Keep retrying until the user pauses the download.</summary>
    Unlimited = 2
}
