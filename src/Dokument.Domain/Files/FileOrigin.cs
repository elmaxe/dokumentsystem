namespace Dokument.Domain.Files;

/// <summary>
/// Where a file version's pages came from, when it was made by merging or splitting PDFs (docs/design.md §8.4): one
/// row per stretch of pages, in page order, for the Info tab's "Made from". The source is named as it was then, so the
/// line still reads right after the source is renamed, trashed or deleted.
/// </summary>
public sealed class FileOrigin
{
    public required Guid VersionId { get; init; }

    /// <summary>The row's place in the list, from 0.</summary>
    public required int Position { get; init; }
    public required PdfOperationKind Kind { get; init; }

    /// <summary>The file the pages were taken from. Not a foreign key: the source can be deleted.</summary>
    public required Guid SourceNodeId { get; init; }
    public required NodeName SourceName { get; init; }

    /// <summary>The pages in the source, 1-based and inclusive.</summary>
    public required int SourceFirst { get; init; }
    public required int SourceLast { get; init; }

    /// <summary>Where they are in this file.</summary>
    public required int First { get; init; }
}
