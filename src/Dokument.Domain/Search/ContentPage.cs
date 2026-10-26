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

    /// <summary>Spoken words in a recording, about a minute per page, with times (§11.1).</summary>
    Transcript,
}

/// <summary>
/// One page of a blob's searchable text: a PDF page, a slide, a whole document for formats without pages, or (page 0)
/// its metadata. Indexed with BM25 (§13.1). Results are files, so the best page of each file is what a search shows.
/// </summary>
public sealed class ContentPage
{
    /// <summary>Page 0 holds the metadata; text starts at 1.</summary>
    public const int MetadataPage = 0;

    /// <summary>The BM25 index's key.</summary>
    public long Id { get; init; }
    public required ContentHash BlobHash { get; init; }
    public required int PageNo { get; init; }
    public required PageSource Source { get; init; }
    public required string Text { get; init; }

    /// <summary>
    /// For recognized text, where each word is on the page, so the viewer can mark hits on a scan: a JSON array of
    /// <c>[x, y, width, height, "word"]</c>, each number a fraction of the page's size. For a transcript, its lines as
    /// they were heard (<see cref="TranscriptLine"/>). Null for other sources.
    /// </summary>
    public string? Words { get; init; }

    /// <summary>For a transcript page, the stretch of the recording it covers, in milliseconds from the start.</summary>
    public int? StartMs { get; init; }
    public int? EndMs { get; init; }
}
