using Dokument.Domain.Files;

namespace Dokument.Domain.Previews;

/// <summary>One image the worker made from a blob (docs/design.md §6). Regenerable, so it never counts toward quota.</summary>
public sealed class Rendition
{
    public required ContentHash BlobHash { get; init; }
    public required RenditionKind Kind { get; init; }
    public required DerivedKey StorageKey { get; init; }
    public string MimeType { get; init; } = "image/webp";
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>For page sheets, how many pages they hold. Null for other kinds.</summary>
    public int? Count { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// How a PDF's page sheets are laid out (docs/design.md §8.4): each page fitted into a 200 × 283 cell (A4 upright) on
/// a transparent background, 10 cells to a row and at most 100 to a sheet, left to right and top to bottom.
/// </summary>
public static class PageSheetLayout
{
    public const int CellWidth = 200;
    public const int CellHeight = 283;
    public const int Columns = 10;
    public const int PerSheet = 100;

    public static int SheetCount(int pages) => (pages + PerSheet - 1) / PerSheet;

    /// <summary>Every file the page sheets of a blob with <paramref name="pages"/> pages are kept in.</summary>
    public static IEnumerable<DerivedKey> Keys(ContentHash hash, int pages) =>
        Enumerable
            .Range(1, Math.Max(1, SheetCount(pages)))
            .Select(s => DerivedKey.ForPageSheet(hash, s));
}
