using System.ComponentModel;
using System.Runtime.CompilerServices;
using Adm.Core.Util;

namespace Adm.Core.Models;

/// <summary>
/// Observable state of one download. The download engine mutates this object from
/// worker threads; WPF's binding engine marshals scalar property changes itself.
/// </summary>
public sealed class DownloadItem : INotifyPropertyChanged
{
    private string _url = string.Empty;
    private string _filePath = string.Empty;
    private string _category = "Other";
    private long _totalBytes;
    private long _bytesDownloaded;
    private double _bytesPerSecond;
    private DownloadStatus _status = DownloadStatus.Queued;
    private string? _errorMessage;
    private int _maxConnections = 8;
    private long _speedLimitBytesPerSecond;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; init; } = Guid.NewGuid();

    public string Url
    {
        get => _url;
        set => Set(ref _url, value);
    }

    /// <summary>Absolute path of the finished file. While downloading the data lives in <see cref="TempPath"/>.</summary>
    public string FilePath
    {
        get => _filePath;
        set
        {
            if (Set(ref _filePath, value))
            {
                Raise(nameof(FileName), nameof(Folder), nameof(TempPath), nameof(CanOpenFolder));
            }
        }
    }

    public string TempPath => FilePath + ".admpart";

    public string FileName => string.IsNullOrEmpty(FilePath) ? "Pending..." : Path.GetFileName(FilePath);

    public string Folder => string.IsNullOrEmpty(FilePath) ? string.Empty : Path.GetDirectoryName(FilePath) ?? string.Empty;

    public string Category
    {
        get => _category;
        set => Set(ref _category, value);
    }

    public long TotalBytes
    {
        get => _totalBytes;
        set
        {
            if (Set(ref _totalBytes, value))
            {
                Raise(nameof(Progress), nameof(ProgressPercent), nameof(SizeText), nameof(EtaText), nameof(IsIndeterminate),
                    nameof(DetailText));
            }
        }
    }

    public long BytesDownloaded
    {
        get => _bytesDownloaded;
        set
        {
            if (Set(ref _bytesDownloaded, value))
            {
                Raise(nameof(Progress), nameof(ProgressPercent), nameof(SizeText), nameof(EtaText), nameof(DetailText));
            }
        }
    }

    public double BytesPerSecond
    {
        get => _bytesPerSecond;
        set
        {
            if (Set(ref _bytesPerSecond, value))
            {
                Raise(nameof(SpeedText), nameof(EtaText), nameof(DetailText));
            }
        }
    }

    public DownloadStatus Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
            {
                Raise(nameof(StatusText), nameof(IsActive), nameof(CanPause), nameof(CanResume), nameof(IsIndeterminate),
                    nameof(CanOpen), nameof(SpeedText), nameof(DetailText));
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (Set(ref _errorMessage, value))
            {
                Raise(nameof(HasError));
            }
        }
    }

    public int MaxConnections
    {
        get => _maxConnections;
        set => Set(ref _maxConnections, value);
    }

    public long SpeedLimitBytesPerSecond
    {
        get => _speedLimitBytesPerSecond;
        set => Set(ref _speedLimitBytesPerSecond, value);
    }

    public bool SupportsRanges { get; set; }

    /// <summary>Which engine runs this item.</summary>
    public DownloadKind Kind { get; set; } = DownloadKind.Http;

    // yt-dlp settings, copied from the picker when the item is queued.
    public string? VideoSelector { get; set; }

    public string? VideoFormatLabel { get; set; }

    public string? VideoMergeContainer { get; set; }

    public bool VideoAudioOnly { get; set; }

    public bool VideoConvertToMp3 { get; set; }

    public bool IsVideo => Kind == DownloadKind.Video;

    public string? ETag { get; set; }

    public string? LastModified { get; set; }

    public string? Referrer { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>Cookie header sent with this download, forwarded from the browser.</summary>
    public string? Cookie { get; set; }

    /// <summary>Value for the Authorization header, when the server needs one.</summary>
    public string? Authorization { get; set; }

    /// <summary>Lower number means the download starts earlier in the queue.</summary>
    public int Priority { get; set; }

    /// <summary>Expected hash of the finished file, used to verify the download.</summary>
    public string? ExpectedChecksum { get; set; }

    /// <summary>Algorithm of <see cref="ExpectedChecksum"/>.</summary>
    public ChecksumAlgorithm ChecksumAlgorithm { get; set; } = ChecksumAlgorithm.None;

    /// <summary>Number of times this download has been retried after a failure.</summary>
    public int RetryCount { get; set; }

    /// <summary>Set when the transfer failed and will be retried automatically.</summary>
    public bool IsRetryScheduled { get; set; }

    /// <summary>
    /// The earliest moment a scheduled retry may start. The queue skips the
    /// item until then, so a failing server is not hammered.
    /// </summary>
    public DateTimeOffset RetryNotBefore { get; set; }

    /// <summary>True once the finished file matched its expected checksum.</summary>
    public bool? ChecksumVerified { get; set; }

    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.Now;

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Segment bookkeeping used for pause/resume. Not shown in the UI.</summary>
    public List<SegmentRecord> Segments { get; set; } = new();

    public double Progress => TotalBytes > 0 ? Math.Clamp((double)BytesDownloaded / TotalBytes, 0, 1) : 0;

    public double ProgressPercent => Progress * 100;

    public bool IsIndeterminate => TotalBytes <= 0 && IsActive;

    public bool IsActive => Status is DownloadStatus.Connecting or DownloadStatus.Downloading;

    public bool CanPause => IsActive || Status == DownloadStatus.Queued;

    public bool CanResume => Status is DownloadStatus.Paused or DownloadStatus.Failed;

    public bool CanOpen => Status == DownloadStatus.Completed && File.Exists(FilePath);

    public bool CanOpenFolder => !string.IsNullOrEmpty(FilePath);

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public string SizeText
    {
        get
        {
            if (TotalBytes > 0)
                return $"{Format.Bytes(BytesDownloaded)} of {Format.Bytes(TotalBytes)}";

            return BytesDownloaded > 0 ? Format.Bytes(BytesDownloaded) : string.Empty;
        }
    }

    public string SpeedText => IsActive ? Format.Speed(BytesPerSecond) : string.Empty;

    public string EtaText
    {
        get
        {
            if (!IsActive || TotalBytes <= 0 || BytesPerSecond < 1)
                return string.Empty;

            double seconds = (TotalBytes - BytesDownloaded) / BytesPerSecond;
            return Format.TimeLeft(TimeSpan.FromSeconds(Math.Max(0, seconds)));
        }
    }

    /// <summary>Single line summary shown under the file name.</summary>
    public string DetailText
    {
        get
        {
            var parts = new List<string>(4);

            if (!string.IsNullOrWhiteSpace(VideoFormatLabel))
                parts.Add(VideoFormatLabel);

            if (!string.IsNullOrWhiteSpace(StatusText))
                parts.Add(StatusText);

            if (!string.IsNullOrWhiteSpace(SizeText))
                parts.Add(SizeText);

            if (!string.IsNullOrWhiteSpace(SpeedText))
                parts.Add(SpeedText);

            if (IsActive && !string.IsNullOrWhiteSpace(EtaText))
                parts.Add(EtaText);

            if (IsActive && Segments.Count > 1)
                parts.Add($"{Segments.Count} parts");

            if (ChecksumVerified == true)
                parts.Add("Checksum OK");
            else if (ChecksumVerified == false)
                parts.Add("Checksum failed");

            if (RetryCount > 0 && Status == DownloadStatus.Failed)
                parts.Add($"{RetryCount} retries");

            return string.Join("  ·  ", parts);
        }
    }

    public string StatusText => Status switch
    {
        DownloadStatus.Queued => "Queued",
        DownloadStatus.Connecting => "Connecting...",
        DownloadStatus.Downloading => "Downloading",
        DownloadStatus.Paused => "Paused",
        DownloadStatus.Completed => "Completed",
        DownloadStatus.Failed => string.IsNullOrWhiteSpace(ErrorMessage) ? "Failed" : $"Failed · {ErrorMessage}",
        _ => Status.ToString()
    };

    /// <summary>Raises projections after the engine updates segment state directly.</summary>
    public void RefreshProgress()
    {
        Raise(nameof(Progress), nameof(ProgressPercent), nameof(SizeText), nameof(EtaText), nameof(SpeedText),
            nameof(StatusText), nameof(DetailText), nameof(IsIndeterminate));
    }

    public override string ToString() => $"{FileName} ({Url})";

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        Raise(propertyName);
        return true;
    }

    private void Raise(params string?[] names)
    {
        var handler = PropertyChanged;
        if (handler is null)
            return;

        foreach (var name in names)
            handler.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
