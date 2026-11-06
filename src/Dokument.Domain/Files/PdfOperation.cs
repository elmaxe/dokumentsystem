namespace Dokument.Domain.Files;

public enum PdfOperationKind
{
    /// <summary>Several PDFs into one new file.</summary>
    Merge,

    /// <summary>One PDF into new files, one per part.</summary>
    Split,
}

public enum PdfOperationState
{
    /// <summary>Queued for the worker.</summary>
    Waiting,

    /// <summary>The worker is putting the pages together.</summary>
    Working,

    /// <summary>The new PDFs are made; the server is adding them to the drive.</summary>
    Saving,

    /// <summary>The new files are in the drive; see <see cref="PdfOperation.Results"/>.</summary>
    Done,

    /// <summary>Nothing was made; see <see cref="PdfOperation.Error"/>.</summary>
    Failed,

    /// <summary>The person stopped it before it saved anything.</summary>
    Cancelled,
}

/// <summary>A file the operation reads, pinned to the version that was current when it was asked for.</summary>
/// <param name="StorageKey">Where the bytes are on the originals volume.</param>
public sealed record PdfSource(
    Guid NodeId,
    Guid VersionId,
    string BlobHash,
    string StorageKey,
    string Name,
    long Size,
    int PageCount
);

/// <summary>Pages <see cref="First"/> to <see cref="Last"/> (1-based, inclusive) of source number <see cref="Source"/>.</summary>
public sealed record PdfPageRange(int Source, int First, int Last)
{
    public int Count => Last - First + 1;
}

/// <summary>One new file: its name and its pages, in order.</summary>
public sealed record PdfPart(string Name, List<PdfPageRange> Pages)
{
    public int PageCount => Pages.Sum(p => p.Count);
}

/// <summary>A PDF the worker made, waiting on the derived volume for the server to add it to the drive.</summary>
public sealed record PdfMade(string Path, string BlobHash, long Size);

/// <summary>A file the operation added. <see cref="DuplicateOf"/> is a file the owner already had with the same bytes.</summary>
public sealed record PdfResult(Guid NodeId, string Name, Guid? DuplicateOf);

/// <summary>
/// Merging PDFs into one, or splitting one into parts (docs/design.md §8.4). The request pins the sources' current
/// versions and reserves quota; the worker makes the PDFs with PDFium, since it parses untrusted files in its sandbox
/// (§10.4); the server then adds them as new files, because only it may write to the originals volume. Merge and split
/// are the same thing underneath: a list of parts, each a list of page ranges.
/// </summary>
public sealed class PdfOperation
{
    /// <summary>Most files one merge takes.</summary>
    public const int MaxSources = 100;

    /// <summary>Most pages one operation makes in all.</summary>
    public const int MaxPages = 2000;

    /// <summary>Most files one split makes.</summary>
    public const int MaxParts = 500;

    /// <summary>Finished operations are forgotten after this long; the files they made stay.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);

    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required PdfOperationKind Kind { get; init; }

    /// <summary>Who asked for it.</summary>
    public required Guid UserId { get; init; }

    /// <summary>Whose files the results become and whose quota is reserved: the folder's owner (§9).</summary>
    public required Guid OwnerId { get; init; }

    /// <summary>Where the new files go; null for the top of My files.</summary>
    public Guid? FolderId { get; init; }

    /// <summary>For a split: a new folder in <see cref="FolderId"/> to put the parts in.</summary>
    public NodeName? NewFolderName { get; init; }

    /// <summary>Moves the sources to the trash once the new files are saved.</summary>
    public bool TrashSources { get; init; }
    public required List<PdfSource> Sources { get; init; }
    public required List<PdfPart> Parts { get; init; }
    public PdfOperationState State { get; set; } = PdfOperationState.Waiting;

    /// <summary>Pages put together so far, of <see cref="PagesTotal"/>.</summary>
    public int PagesDone { get; set; }
    public int PagesTotal { get; init; }

    /// <summary>Quota held until the files are saved: the sources' total size.</summary>
    public long ReservedBytes { get; set; }
    public List<PdfMade> Made { get; set; } = [];
    public List<PdfResult> Results { get; set; } = [];
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; set; }

    public bool IsActive =>
        State is PdfOperationState.Waiting or PdfOperationState.Working or PdfOperationState.Saving;
}

/// <summary>
/// Page ranges as people type them: "1-3, 5, 7-10" (docs/design.md §8.4). En and em dashes count as hyphens, and
/// spaces don't matter. Ranges keep the order they're given in.
/// </summary>
public static class PageRanges
{
    public static bool TryParse(
        string? text,
        int pageCount,
        out List<(int First, int Last)> ranges,
        out string? error
    )
    {
        ranges = [];
        error = null;
        var items = (text ?? "")
            .Replace('–', '-')
            .Replace('—', '-')
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (items.Length == 0)
        {
            error = "Give at least one page.";
            return false;
        }

        foreach (var item in items)
        {
            var ends = item.Split('-', StringSplitOptions.TrimEntries);
            if (
                ends.Length is < 1 or > 2
                || !int.TryParse(ends[0], out var first)
                || !int.TryParse(ends[^1], out var last)
            )
            {
                error = $"\"{item}\" isn't a page or a range of pages.";
                return false;
            }

            if (first < 1 || last > pageCount || first > last)
            {
                error =
                    first > last
                        ? $"\"{item}\" goes backwards."
                        : $"\"{item}\" is outside pages 1 to {pageCount}.";
                return false;
            }

            ranges.Add((first, last));
        }

        return true;
    }
}
