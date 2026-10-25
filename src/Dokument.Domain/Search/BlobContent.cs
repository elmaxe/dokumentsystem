using Dokument.Domain.Files;

namespace Dokument.Domain.Search;

public enum ContentStatus
{
    /// <summary>Text was found; see the blob's content pages.</summary>
    Ready,

    /// <summary>A format the stage reads, but there was no text in it, such as a photo of a beach.</summary>
    Empty,

    /// <summary>Not a format the stage reads, such as a zip. Only the name and metadata are searchable.</summary>
    Unsupported,

    /// <summary>The file looked readable but wasn't, e.g. a corrupt or password-protected PDF.</summary>
    Failed,
}

/// <summary>
/// What the text stage (docs/design.md §10.2, §13.2) made of a blob, and with which <see cref="PipelineVersion"/>. Like
/// thumbnails, text belongs to the bytes, so identical content is read once and a restored version is searchable at
/// once. A blob with no row, or an older version, is still to be read; the worker backfills those at low priority.
/// </summary>
public sealed class BlobContent
{
    /// <summary>Bump when extraction gets better, so every blob is read again in the background.</summary>
    public const int CurrentPipelineVersion = 2;

    public required ContentHash BlobHash { get; init; }
    public required ContentStatus Status { get; set; }
    public int PipelineVersion { get; set; } = CurrentPipelineVersion;

    /// <summary>Pages for a PDF, slides for a presentation; null for formats without pages.</summary>
    public int? PageCount { get; set; }

    /// <summary>
    /// Pages or images that would have needed text recognition while it was turned off. Turning it back on reads these
    /// blobs again.
    /// </summary>
    public bool OcrSkipped { get; set; }

    /// <summary>Which recognizer read the scans, with its version, e.g. "tesseract 5.3.4"; null when nothing was recognized.</summary>
    public string? OcrEngine { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
