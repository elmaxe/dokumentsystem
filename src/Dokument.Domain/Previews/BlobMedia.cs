using Dokument.Domain.Files;

namespace Dokument.Domain.Previews;

/// <summary>How a browser gets to play a video or audio file (docs/design.md §10.3, §14).</summary>
public enum PlaybackPlan
{
    /// <summary>Browsers play the original as it is, e.g. H.264/AAC in MP4 or an MP3.</summary>
    Direct,

    /// <summary>The streams are fine but the container isn't (H.264 in .mov or .mkv): copied into MP4, which takes seconds.</summary>
    Remux,

    /// <summary>The codecs need converting (HEVC, MPEG-2, WMA…): re-encoded to H.264/AAC.</summary>
    Transcode,
}

/// <summary>Whether a blob's playable copy exists. Waiting and running come from the jobs table, like thumbnails.</summary>
public enum CopyStatus
{
    None,
    Ready,

    /// <summary>ffmpeg couldn't make one; the file may be damaged. Not retried until an admin asks.</summary>
    Failed,
}

/// <summary>
/// What ffprobe found in a video or audio blob, and its playable copy. Belongs to the bytes, like thumbnails. Other
/// stages read it too, e.g. transcription only runs for blobs with an audio stream.
/// </summary>
public sealed class BlobMedia
{
    public required ContentHash BlobHash { get; init; }

    /// <summary>ffprobe's container name, e.g. <c>mov</c>, <c>mp4</c>, <c>matroska</c>, <c>mp3</c>.</summary>
    public required string Container { get; set; }
    public double? DurationSeconds { get; set; }
    public bool HasVideo { get; set; }
    public bool HasAudio { get; set; }

    /// <summary>ffprobe's codec names, e.g. <c>hevc</c>, <c>h264</c>, <c>aac</c>.</summary>
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }

    /// <summary>As shown, after the rotation the file asks for.</summary>
    public int? Width { get; set; }
    public int? Height { get; set; }
    public PlaybackPlan Plan { get; set; }
    public CopyStatus CopyStatus { get; set; }

    /// <summary>How far the copy being made has got, 0 to 100. Null when none is being made.</summary>
    public int? CopyProgress { get; set; }

    /// <summary>Why the last copy failed, for admins.</summary>
    public string? CopyError { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public bool NeedsCopy => Plan != PlaybackPlan.Direct;
}

/// <summary>What the API tells clients about a file's media, for the tile's duration and the viewer's choice.</summary>
public enum CopyState
{
    /// <summary>The original plays as it is.</summary>
    NotNeeded,

    /// <summary>A copy is needed and none is being made: copies are off, or made when someone opens the file.</summary>
    None,
    Waiting,
    Making,
    Ready,
    Failed,
}

public sealed record MediaState(
    double? DurationSeconds,
    bool HasVideo,
    bool HasAudio,
    string? VideoCodec,
    CopyState Copy,
    int? Progress,
    SeekPreviewSheet? SeekPreviews = null
);
