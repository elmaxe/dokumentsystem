using Dokument.Domain.Files;

namespace Dokument.Domain.Previews;

public enum PreviewStatus
{
    /// <summary>Thumbnails exist; see the blob's renditions.</summary>
    Ready,

    /// <summary>Not a format the pipeline renders, such as a zip. The grid shows the type icon.</summary>
    Unsupported,

    /// <summary>The file looked renderable but every attempt failed, e.g. a corrupt or password-protected PDF.</summary>
    Failed,
}

/// <summary>
/// What the thumbnail stage (docs/design.md §10.2) made of a blob, and with which <see cref="PipelineVersion"/>. A blob
/// with no row, or an older version, is still to be processed; the worker backfills those at low priority.
/// </summary>
public sealed class BlobPreview
{
    /// <summary>Bump when thumbnails get better, so every blob is redone in the background.</summary>
    public const int CurrentPipelineVersion = 1;

    public required ContentHash BlobHash { get; init; }
    public required PreviewStatus Status { get; set; }
    public int PipelineVersion { get; set; } = CurrentPipelineVersion;

    /// <summary>The source's size in pixels, after EXIF rotation; for a PDF, page 1 in points. Lets clients lay out before the image loads.</summary>
    public int? Width { get; set; }
    public int? Height { get; set; }

    /// <summary>PDFs only.</summary>
    public int? PageCount { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsCurrent => PipelineVersion >= CurrentPipelineVersion;
}

/// <summary>What a file tile shows (docs/design.md §16.4): the thumbnail, a shimmer while it is made, or the type icon.</summary>
public enum ThumbnailState
{
    None,
    Pending,
    Ready,
}
