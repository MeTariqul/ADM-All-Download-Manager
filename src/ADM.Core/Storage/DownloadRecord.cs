using Adm.Core.Models;
using Adm.Core.Util;

namespace Adm.Core.Storage;

/// <summary>Serializable snapshot of a <see cref="DownloadItem"/> for the history file.</summary>
public sealed class DownloadRecord
{
    public Guid Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Category { get; set; } = "Other";
    public long TotalBytes { get; set; }
    public long BytesDownloaded { get; set; }
    public DownloadStatus Status { get; set; }
    public bool SupportsRanges { get; set; }
    public DownloadKind Kind { get; set; }
    public string? VideoSelector { get; set; }
    public string? VideoFormatLabel { get; set; }
    public string? VideoMergeContainer { get; set; }
    public bool VideoAudioOnly { get; set; }
    public bool VideoConvertToMp3 { get; set; }
    public int MaxConnections { get; set; } = 8;
    public long SpeedLimitBytesPerSecond { get; set; }
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public string? Referrer { get; set; }
    public string? UserAgent { get; set; }
    public string? Cookie { get; set; }
    public string? Authorization { get; set; }
    public int Priority { get; set; }
    public string? ExpectedChecksum { get; set; }
    public ChecksumAlgorithm ChecksumAlgorithm { get; set; } = ChecksumAlgorithm.None;
    public DateTimeOffset AddedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public List<SegmentRecord> Segments { get; set; } = new();

    public static DownloadRecord FromItem(DownloadItem item) => new()
    {
        Id = item.Id,
        Url = item.Url,
        FilePath = item.FilePath,
        Category = item.Category,
        TotalBytes = item.TotalBytes,
        BytesDownloaded = item.BytesDownloaded,
        Status = item.Status,
        SupportsRanges = item.SupportsRanges,
        Kind = item.Kind,
        VideoSelector = item.VideoSelector,
        VideoFormatLabel = item.VideoFormatLabel,
        VideoMergeContainer = item.VideoMergeContainer,
        VideoAudioOnly = item.VideoAudioOnly,
        VideoConvertToMp3 = item.VideoConvertToMp3,
        MaxConnections = item.MaxConnections,
        SpeedLimitBytesPerSecond = item.SpeedLimitBytesPerSecond,
        ETag = item.ETag,
        LastModified = item.LastModified,
        Referrer = item.Referrer,
        UserAgent = item.UserAgent,
        Cookie = item.Cookie,
        Authorization = item.Authorization,
        Priority = item.Priority,
        ExpectedChecksum = item.ExpectedChecksum,
        ChecksumAlgorithm = item.ChecksumAlgorithm,
        AddedAt = item.AddedAt,
        CompletedAt = item.CompletedAt,
        Segments = item.Segments
            .Select(s => new SegmentRecord { Start = s.Start, End = s.End, Position = s.Position })
            .ToList()
    };

    public DownloadItem ToItem()
    {
        var item = new DownloadItem
        {
            Id = Id == Guid.Empty ? Guid.NewGuid() : Id,
            Url = Url,
            FilePath = FilePath,
            Category = Category,
            TotalBytes = Math.Max(0, TotalBytes),
            BytesDownloaded = Math.Max(0, BytesDownloaded),
            MaxConnections = MaxConnections is >= 1 and <= SegmentPlannerMaximum ? MaxConnections : 8,
            SpeedLimitBytesPerSecond = Math.Max(0, SpeedLimitBytesPerSecond),
            SupportsRanges = SupportsRanges,
            Kind = Kind,
            VideoSelector = VideoSelector,
            VideoFormatLabel = VideoFormatLabel,
            VideoMergeContainer = VideoMergeContainer,
            VideoAudioOnly = VideoAudioOnly,
            VideoConvertToMp3 = VideoConvertToMp3,
            ETag = ETag,
            LastModified = LastModified,
            Referrer = Referrer,
            UserAgent = UserAgent,
            Cookie = Cookie,
            Authorization = Authorization,
            Priority = Priority,
            ExpectedChecksum = ExpectedChecksum,
            ChecksumAlgorithm = ChecksumAlgorithm,
            AddedAt = AddedAt == default ? DateTimeOffset.Now : AddedAt,
            CompletedAt = CompletedAt,
            Segments = Segments
                .Select(s => new SegmentRecord { Start = s.Start, End = s.End, Position = s.Position })
                .ToList()
        };

        item.Status = Status switch
        {
            DownloadStatus.Completed => DownloadStatus.Completed,
            DownloadStatus.Failed => DownloadStatus.Failed,
            _ => DownloadStatus.Paused
        };

        return item;
    }

    private const int SegmentPlannerMaximum = 32;
}
