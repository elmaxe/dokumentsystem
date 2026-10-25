using Dokument.Domain.Files;

namespace Dokument.Domain.Search;

/// <summary>Where a page's searchable text came from (docs/design.md §13.2).</summary>
public enum PageSource
{
    /// <summary>The file's own text: a PDF's text layer, an Office document, plain text.</summary>
    Text,

    /// <summary>Recognized in a scan or a photo.</summary>
    Ocr,

    /// <summary>Tags such as the title, author, camera or artist. Always page 0.</summary>
    Metadata,

    /// <summary>Text someone added to a page where nothing was read, such as a caption for a photo (§13.5).</summary>
    Added,
}

/// <summary>
/// One page of a blob's searchable text: a PDF page, a slide, a whole document for formats without pages, or (page 0)
/// its metadata. Indexed with BM25 (§13.1). Results are files, so the best page of each file is what a search shows.
/// <para>
/// A page with a <see cref="VersionId"/> is a person's correction for that file version (§13.5) and replaces the read
/// page of the same number for it alone; page 0 of a version holds its edited title and description and adds to the
/// read one. Both kinds sit in one table so one BM25 index serves them.
/// </para>
/// </summary>
public sealed class ContentPage
{
    /// <summary>Page 0 holds the metadata; text starts at 1.</summary>
    public const int MetadataPage = 0;

    /// <summary>The BM25 index's key.</summary>
    public long Id { get; init; }
    public required ContentHash BlobHash { get; init; }

    /// <summary>The version a correction belongs to; null for what the worker read from the bytes.</summary>
    public Guid? VersionId { get; init; }
    public required int PageNo { get; init; }

    /// <summary>For a correction, where the page it replaces came from, or <see cref="PageSource.Added"/>.</summary>
    public required PageSource Source { get; init; }
    public required string Text { get; set; }

    /// <summary>Who last corrected the page, and when; null for read pages.</summary>
    public Guid? EditedBy { get; set; }
    public DateTimeOffset? EditedAt { get; set; }

    /// <summary>
    /// For recognized text, where each word is on the page, so the viewer can mark hits on a scan: a JSON array of
    /// <c>[x, y, width, height, "word"]</c>, each number a fraction of the page's size. Null for other sources.
    /// </summary>
    public string? Words { get; init; }
}
