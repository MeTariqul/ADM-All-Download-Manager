using Adm.Core.Util;

namespace Adm.Core.Media;

/// <summary>
/// One choice shown in the video format picker. <see cref="Selector"/> is passed
/// to yt-dlp's <c>-f</c> option, so it can describe either a single format or a
/// video+audio merge.
/// </summary>
public sealed class VideoFormat
{
    public string Selector { get; init; } = "best";

    /// <summary>Short quality label, for example <c>1080p60</c> or <c>Audio only</c>.</summary>
    public string ResolutionText { get; init; } = "Best available";

    /// <summary>Container and codec summary, for example <c>MP4 · H.264</c>.</summary>
    public string FormatText { get; init; } = string.Empty;

    /// <summary>Expected final extension (mp4, mkv, m4a, mp3, ...).</summary>
    public string? Extension { get; init; }

    /// <summary>Container passed to <c>--merge-output-format</c>, or null when no merge is needed.</summary>
    public string? MergeContainer { get; init; }

    public long EstimatedBytes { get; init; }

    public bool IsAudioOnly { get; init; }

    public bool ConvertToMp3 { get; init; }

    public bool IsRecommended { get; init; }

    public bool NeedsMerge => !IsAudioOnly && !string.IsNullOrWhiteSpace(MergeContainer);

    public string SizeText => EstimatedBytes > 0 ? "~" + Format.Bytes(EstimatedBytes) : "Size unknown";

    public override string ToString() =>
        string.IsNullOrWhiteSpace(FormatText)
            ? $"{ResolutionText} · {SizeText}"
            : $"{ResolutionText} · {FormatText} · {SizeText}";
}
