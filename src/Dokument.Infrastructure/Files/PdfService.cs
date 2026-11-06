using System.Text.Json.Nodes;
using Dokument.Application.Files;
using Dokument.Domain.Audit;
using Dokument.Domain.Files;
using Dokument.Domain.Jobs;
using Dokument.Domain.Previews;
using Dokument.Domain.Search;
using Dokument.Domain.Tags;
using Dokument.Infrastructure.Jobs;
using Dokument.Infrastructure.Live;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Previews;
using Dokument.Infrastructure.Search;
using Dokument.Infrastructure.Tags;
using Microsoft.EntityFrameworkCore;

namespace Dokument.Infrastructure.Files;

/// <summary>One page of a new PDF: where it comes from.</summary>
/// <param name="Source">Index into the operation's sources.</param>
/// <param name="SourcePage">1-based page in that source.</param>
/// <param name="Page">1-based page in the new file.</param>
internal sealed record PageMapping(int Source, int SourcePage, int Page);

/// <summary>A source of a "Made from" line, as it is now.</summary>
public enum OriginSourceState
{
    Live,
    Trashed,
    Gone,
}

public sealed record OriginLine(
    Guid SourceNodeId,
    string SourceName,
    int SourceFirst,
    int SourceLast,
    int First,
    int Last,
    OriginSourceState State
);

public sealed record FileOriginView(
    PdfOperationKind Kind,
    DateTimeOffset At,
    string ByName,
    List<OriginLine> Lines
);

/// <summary>
/// Merging and splitting PDFs (docs/design.md §8.4). The request checks the sources, pins their current versions,
/// reserves quota and queues a <see cref="JobType.Pdf"/> job; the worker makes the PDFs; then the server adds them with
/// <see cref="SaveAsync"/>, copying each page's text, the person's text edits and tags from the sources, so nothing is
/// read or recognized twice.
/// </summary>
public sealed class PdfService(
    DokumentDbContext db,
    DriveService drive,
    TrashService trash,
    BlobStore blobs,
    DerivedStore derived,
    JobQueue jobs,
    TagRules rules,
    LiveChanges live,
    TimeProvider time
)
{
    public const string PdfMimeType = "application/pdf";

    public async Task<PdfOperation> StartMergeAsync(
        Actor actor,
        IReadOnlyList<Guid> ids,
        Guid? folderId,
        string? name,
        bool trashSources,
        CancellationToken ct = default
    )
    {
        var fileName = DriveService.ParseName(name);
        if (ids.Count < 2 || ids.Count > PdfOperation.MaxSources)
        {
            throw new DriveException(
                DriveError.InvalidRequest,
                $"Choose 2 to {PdfOperation.MaxSources} PDFs to merge."
            );
        }

        if (ids.Distinct().Count() != ids.Count)
        {
            throw new DriveException(DriveError.InvalidRequest, "A file is listed twice.");
        }

        var sources = new List<PdfSource>();
        foreach (var id in ids)
        {
            sources.Add(await SourceAsync(actor, id, ct));
        }

        var part = new PdfPart(
            fileName.Value,
            sources.Select((s, i) => new PdfPageRange(i, 1, s.PageCount)).ToList()
        );
        var folder = await drive.GetFolderAsync(actor, folderId, ct);
        return await StartAsync(
            actor,
            PdfOperationKind.Merge,
            folder,
            newFolderName: null,
            trashSources,
            sources,
            [part],
            ct
        );
    }

    public async Task<PdfOperation> StartSplitAsync(
        Actor actor,
        Guid id,
        IReadOnlyList<(string? Name, string? Pages)> parts,
        string? newFolderName,
        bool trashSource,
        CancellationToken ct = default
    )
    {
        if (parts.Count < 1 || parts.Count > PdfOperation.MaxParts)
        {
            throw new DriveException(
                DriveError.InvalidRequest,
                $"Give 1 to {PdfOperation.MaxParts} parts."
            );
        }

        var source = await SourceAsync(actor, id, ct);
        var made = new List<PdfPart>();
        foreach (var (name, pages) in parts)
        {
            var partName = DriveService.ParseName(name);
            if (!PageRanges.TryParse(pages, source.PageCount, out var ranges, out var error))
            {
                throw new DriveException(DriveError.InvalidRequest, $"{partName}: {error}");
            }

            made.Add(
                new PdfPart(
                    partName.Value,
                    ranges.Select(r => new PdfPageRange(0, r.First, r.Last)).ToList()
                )
            );
        }

        var node = await drive.GetFileAsync(actor, id, ct);
        var folder = await drive.GetFolderAsync(actor, node.ParentId, ct);
        return await StartAsync(
            actor,
            PdfOperationKind.Split,
            folder,
            newFolderName is { Length: > 0 } ? DriveService.ParseName(newFolderName) : null,
            trashSource,
            [source],
            made,
            ct
        );
    }

    /// <summary>The person's own operations that are still running or finished in the last week, newest first.</summary>
    public Task<List<PdfOperation>> ListAsync(Actor actor, CancellationToken ct = default)
    {
        var since = time.GetUtcNow() - PdfOperation.KeepFor;
        return db
            .PdfOperations.AsNoTracking()
            .Where(o => o.UserId == actor.UserId && o.CreatedAt >= since)
            .OrderByDescending(o => o.CreatedAt)
            .Take(20)
            .ToListAsync(ct);
    }

    public async Task<PdfOperation> GetAsync(
        Actor actor,
        Guid id,
        CancellationToken ct = default
    ) =>
        await db
            .PdfOperations.AsNoTracking()
            .SingleOrDefaultAsync(o => o.Id == id && o.UserId == actor.UserId, ct)
        ?? throw DriveException.NotFound("merge or split");

    /// <summary>Stops an operation that hasn't saved anything yet. One that is saving or finished is left as it is.</summary>
    public async Task<PdfOperation> CancelAsync(
        Actor actor,
        Guid id,
        CancellationToken ct = default
    )
    {
        var op = await GetAsync(actor, id, ct);
        if (op.State is PdfOperationState.Waiting or PdfOperationState.Working)
        {
            await EndAsync(id, PdfOperationState.Cancelled, error: null, ct);
        }

        return await GetAsync(actor, id, ct);
    }

    /// <summary>
    /// For the worker: takes the operation from waiting to working. Null when there is nothing to do, because it was
    /// cancelled or already went further.
    /// </summary>
    public async Task<PdfOperation?> BeginAsync(Guid id, CancellationToken ct = default)
    {
        var started = await db
            .PdfOperations.Where(o =>
                o.Id == id
                && (o.State == PdfOperationState.Waiting || o.State == PdfOperationState.Working)
            )
            .ExecuteUpdateAsync(
                s =>
                    s.SetProperty(o => o.State, PdfOperationState.Working)
                        .SetProperty(o => o.PagesDone, 0),
                ct
            );
        return started == 0
            ? null
            : await db.PdfOperations.AsNoTracking().SingleAsync(o => o.Id == id, ct);
    }

    public Task ProgressAsync(Guid id, int pagesDone, CancellationToken ct = default) =>
        db
            .PdfOperations.Where(o => o.Id == id && o.State == PdfOperationState.Working)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.PagesDone, pagesDone), ct);

    /// <summary>
    /// For the worker: the PDFs are on the derived volume. Hands the operation to the server, unless it was cancelled
    /// meanwhile. False in that case.
    /// </summary>
    public async Task<bool> MadeAsync(Guid id, List<PdfMade> made, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var op = await db.PdfOperations.SingleOrDefaultAsync(
            o => o.Id == id && o.State == PdfOperationState.Working,
            ct
        );
        if (op is null)
        {
            return false;
        }

        op.Made = made;
        op.State = PdfOperationState.Saving;
        op.PagesDone = op.PagesTotal;
        await jobs.EnqueueAsync(JobType.PdfSave, id.ToString(), JobPriority.Interactive, ct);
        await db.SaveChangesAsync(ct);
        await live.NotifyAsync(op.UserId, LiveTopics.Files, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>Gives up on an operation: nothing more is saved, and the quota still held is released.</summary>
    public Task FailAsync(Guid id, string error, CancellationToken ct = default) =>
        EndAsync(id, PdfOperationState.Failed, error, ct);

    /// <summary>
    /// For the server: adds the PDFs the worker made to the drive as new files, with the sources' text, the person's
    /// text edits, details and tags, and where each page came from. Moves the sources to the trash if asked.
    /// </summary>
    public async Task SaveAsync(Guid id, CancellationToken ct = default)
    {
        var op = await db.PdfOperations.SingleOrDefaultAsync(
            o => o.Id == id && o.State == PdfOperationState.Saving,
            ct
        );
        if (op is null)
        {
            return;
        }

        var actor = new Actor(op.UserId);
        var outputs = new List<(PdfPart Part, string Path, ContentHash Hash, long Size)>();
        for (var i = 0; i < op.Parts.Count; i++)
        {
            // Hashed again here rather than taken from the worker, which parses untrusted files (§10.4).
            var path = Path.Combine(derived.PdfWorkPath(id), Path.GetFileName(op.Made[i].Path));
            await using var file = File.OpenRead(path);
            outputs.Add((op.Parts[i], path, await ContentHash.ComputeAsync(file, ct), file.Length));
        }

        try
        {
            await drive.EnsureQuotaFitsAsync(
                op.OwnerId,
                outputs.Skip(op.Results.Count).Sum(o => o.Size) - op.ReservedBytes,
                ct
            );
        }
        catch (DriveException e) when (e.Error == DriveError.QuotaExceeded)
        {
            await FailAsync(id, "There isn't enough storage left for the new files.", ct);
            return;
        }

        Guid? folderId;
        try
        {
            // A save that failed halfway made its new folder already; the files in it say which it is.
            folderId =
                op.Results.Count > 0
                    ? await db
                        .Nodes.Where(n => n.Id == op.Results[0].NodeId)
                        .Select(n => n.ParentId)
                        .SingleAsync(ct)
                    : await TargetFolderAsync(actor, op, ct);
        }
        catch (DriveException e) when (e.Error == DriveError.NotFound)
        {
            await FailAsync(
                id,
                "The folder for the new files was moved to the trash or deleted.",
                ct
            );
            return;
        }

        var texted = new List<ContentHash>();
        // And it carries on after the files it already added.
        foreach (var (part, path, hash, size) in outputs.Skip(op.Results.Count))
        {
            var pages = Pages(part);
            if (await CopyTextAsync(op, part, pages, hash, ct))
            {
                texted.Add(hash);
            }

            await CopyMetadataAsync(op, part, hash, ct);

            var reserved = Math.Min(op.ReservedBytes, size);
            op.ReservedBytes -= reserved;
            var added = await drive.AddFileAsync(
                actor,
                new NewFile(
                    folderId,
                    TargetNodeId: null,
                    NodeName.From(part.Name),
                    hash,
                    size,
                    PdfMimeType,
                    ClientModifiedAt: null,
                    NameConflictPolicy.KeepBoth
                ),
                reserved,
                op.Kind == PdfOperationKind.Merge ? AuditActions.PdfMerge : AuditActions.PdfSplit,
                storeBytes: ownerId => blobs.Import(path, ownerId, hash),
                ct,
                byFilingRules: false,
                new JsonObject { ["sources"] = AuditSources(op, part) }
            );
            await CarryAsync(actor, op, part, pages, added, ct);
            var duplicate = await db
                .Nodes.Where(n =>
                    n.OwnerId == op.OwnerId
                    && n.Id != added.Node.Id
                    && n.CurrentVersion != null
                    && n.CurrentVersion.BlobHash == hash
                )
                .OrderBy(n => n.TrashedAt != null)
                .ThenBy(n => n.CreatedAt)
                .Select(n => (Guid?)n.Id)
                .FirstOrDefaultAsync(ct);
            op.Results =
            [
                .. op.Results,
                new PdfResult(added.Node.Id, added.Node.Name.Value, duplicate),
            ];
            await db.SaveChangesAsync(ct);
        }

        foreach (var hash in texted)
        {
            // Keyword rules run on the copied text, as they do after the text stage (§13.4).
            await rules.ApplyToBlobAsync(hash, ct);
        }

        if (op.TrashSources)
        {
            foreach (var source in op.Sources)
            {
                try
                {
                    await trash.TrashAsync(actor, [source.NodeId], ct);
                }
                catch (DriveException e) when (e.Error == DriveError.NotFound)
                {
                    // Already trashed or deleted meanwhile.
                }
            }
        }

        await EndAsync(id, PdfOperationState.Done, error: null, ct);
    }

    /// <summary>"Made from" in the Info tab: where a file version's pages came from, if it was made by merging or splitting.</summary>
    public async Task<FileOriginView?> OriginAsync(
        Actor actor,
        Guid nodeId,
        CancellationToken ct = default
    )
    {
        var node = await drive.GetFileAsync(actor, nodeId, ct);
        var rows = await db
            .FileOrigins.AsNoTracking()
            .Where(o => o.VersionId == node.CurrentVersionId)
            .OrderBy(o => o.Position)
            .ToListAsync(ct);
        if (rows.Count == 0 || node.CurrentVersion is not { } version)
        {
            return null;
        }

        var ids = rows.Select(r => r.SourceNodeId).Distinct().ToList();
        var states = await db
            .Nodes.VisibleTo(actor)
            .Where(n => ids.Contains(n.Id))
            .ToDictionaryAsync(n => n.Id, n => n.TrashedAt == null, ct);
        var by = await db
            .Users.Where(u => u.Id == version.UploadedBy)
            .Select(u => u.DisplayName)
            .SingleOrDefaultAsync(ct);
        return new FileOriginView(
            rows[0].Kind,
            version.UploadedAt,
            by ?? "",
            rows.Select(r => new OriginLine(
                    r.SourceNodeId,
                    r.SourceName.Value,
                    r.SourceFirst,
                    r.SourceLast,
                    r.First,
                    r.First + r.SourceLast - r.SourceFirst,
                    states.TryGetValue(r.SourceNodeId, out var live)
                        ? live
                            ? OriginSourceState.Live
                            : OriginSourceState.Trashed
                        : OriginSourceState.Gone
                ))
                .ToList()
        );
    }

    /// <summary>Forgets operations that finished a week ago, and deletes what they left on the derived volume.</summary>
    public async Task<int> PruneAsync(CancellationToken ct = default)
    {
        var before = time.GetUtcNow() - PdfOperation.KeepFor;
        var old = await db
            .PdfOperations.Where(o => o.FinishedAt != null && o.FinishedAt < before)
            .Select(o => o.Id)
            .ToListAsync(ct);
        foreach (var id in old)
        {
            derived.DeletePdfWork(id);
        }

        return await db.PdfOperations.Where(o => old.Contains(o.Id)).ExecuteDeleteAsync(ct);
    }

    private async Task<PdfSource> SourceAsync(Actor actor, Guid id, CancellationToken ct)
    {
        var node = await drive.GetFileAsync(actor, id, ct);
        var version = node.CurrentVersion!;
        if (!string.Equals(version.MimeType, PdfMimeType, StringComparison.OrdinalIgnoreCase))
        {
            throw new DriveException(DriveError.InvalidRequest, $"“{node.Name}” isn't a PDF.");
        }

        var preview = await db
            .BlobPreviews.AsNoTracking()
            .SingleOrDefaultAsync(p => p.BlobHash == version.BlobHash, ct);
        if (preview is { Status: PreviewStatus.Failed })
        {
            throw new DriveException(
                DriveError.InvalidRequest,
                $"“{node.Name}” can't be read. It may be damaged or locked with a password."
            );
        }

        if (preview?.PageCount is not { } pageCount)
        {
            throw new DriveException(
                DriveError.InvalidRequest,
                $"“{node.Name}” is still being processed. Try again in a moment."
            );
        }

        var storageKey = await db
            .Blobs.Where(b => b.OwnerId == node.OwnerId && b.Hash == version.BlobHash)
            .Select(b => b.StorageKey)
            .SingleAsync(ct);
        return new PdfSource(
            node.Id,
            version.Id,
            version.BlobHash.Hex,
            storageKey.Value,
            node.Name.Value,
            version.Size,
            pageCount
        );
    }

    private async Task<PdfOperation> StartAsync(
        Actor actor,
        PdfOperationKind kind,
        Node? folder,
        NodeName? newFolderName,
        bool trashSources,
        List<PdfSource> sources,
        List<PdfPart> parts,
        CancellationToken ct
    )
    {
        var pages = parts.Sum(p => p.PageCount);
        if (pages > PdfOperation.MaxPages)
        {
            throw new DriveException(
                DriveError.InvalidRequest,
                $"That would make {pages} pages; the most is {PdfOperation.MaxPages}."
            );
        }

        var ownerId = folder?.OwnerId ?? actor.UserId;
        var op = new PdfOperation
        {
            Kind = kind,
            UserId = actor.UserId,
            OwnerId = ownerId,
            FolderId = folder?.Id,
            NewFolderName = newFolderName,
            TrashSources = trashSources,
            Sources = sources,
            Parts = parts,
            PagesTotal = pages,
            ReservedBytes = sources.Sum(s => s.Size),
            CreatedAt = time.GetUtcNow(),
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await drive.ReserveQuotaAsync(ownerId, op.ReservedBytes, ct);
        db.PdfOperations.Add(op);
        await jobs.EnqueueAsync(JobType.Pdf, op.Id.ToString(), JobPriority.Interactive, ct);
        await db.SaveChangesAsync(ct);
        await live.NotifyAsync(ownerId, LiveTopics.Storage, ct);
        await tx.CommitAsync(ct);
        return op;
    }

    /// <summary>Finishes an operation that is still going: releases what quota it holds and deletes its work files.</summary>
    private async Task EndAsync(
        Guid id,
        PdfOperationState state,
        string? error,
        CancellationToken ct
    )
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var op = await db.PdfOperations.SingleOrDefaultAsync(o => o.Id == id, ct);
        if (op is null || !op.IsActive)
        {
            return;
        }

        await drive.SettleQuotaAsync(op.OwnerId, op.ReservedBytes, addedBytes: 0, ct);
        op.ReservedBytes = 0;
        op.State = state;
        op.Error = error;
        op.FinishedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await live.NotifyAsync([op.OwnerId, op.UserId], LiveTopics.Files | LiveTopics.Storage, ct);
        await tx.CommitAsync(ct);
        derived.DeletePdfWork(id);
    }

    /// <summary>The folder the new files go in: the operation's, or for a split into a new folder, that folder, made now.</summary>
    private async Task<Guid?> TargetFolderAsync(Actor actor, PdfOperation op, CancellationToken ct)
    {
        var parent = await drive.GetFolderAsync(actor, op.FolderId, ct);
        if (op.NewFolderName is not { } name)
        {
            return parent?.Id;
        }

        if (await drive.FindByNameAsync(op.OwnerId, op.FolderId, name, ct) is not null)
        {
            name = await drive.FreeNameAsync(op.OwnerId, op.FolderId, name, ct);
        }

        return (await drive.CreateFolderAsync(actor, op.FolderId, name.Value, ct)).Id;
    }

    /// <summary>What the audit event says a new file was made from: each source version and its pages.</summary>
    private static JsonArray AuditSources(PdfOperation op, PdfPart part) =>
        new(
            part.Pages.Select(r =>
                    (JsonNode)
                        new JsonObject
                        {
                            ["nodeId"] = op.Sources[r.Source].NodeId,
                            ["versionId"] = op.Sources[r.Source].VersionId,
                            ["pages"] = $"{r.First}-{r.Last}",
                        }
                )
                .ToArray()
        );

    private static ContentHash HashOf(PdfSource source) =>
        ContentHash.TryParse(source.BlobHash, out var hash)
            ? hash
            : throw new InvalidOperationException("A source's hash isn't valid.");

    internal static List<PageMapping> Pages(PdfPart part)
    {
        var pages = new List<PageMapping>();
        foreach (var range in part.Pages)
        {
            for (var p = range.First; p <= range.Last; p++)
            {
                pages.Add(new PageMapping(range.Source, p, pages.Count + 1));
            }
        }

        return pages;
    }

    /// <summary>
    /// Gives the new bytes the text the sources' pages have, so the text stage doesn't read or recognize them again.
    /// Only when every source has been read and the bytes have no text yet. The caller saves. True when it copied.
    /// </summary>
    /// <summary>
    /// Gives the new file what exiftool read from its first source, so its details are the source's and not what PDFium
    /// writes into a new document (its own name as producer, the time as created). Done before the file is added, so no
    /// metadata job is queued. Without a reading of the source, the metadata stage reads the new file as usual.
    /// </summary>
    private async Task CopyMetadataAsync(
        PdfOperation op,
        PdfPart part,
        ContentHash hash,
        CancellationToken ct
    )
    {
        if (await db.BlobMetadata.AnyAsync(m => m.BlobHash == hash, ct))
        {
            return;
        }

        var first = HashOf(op.Sources[part.Pages[0].Source]);
        var source = await db
            .BlobMetadata.AsNoTracking()
            .FirstOrDefaultAsync(
                m =>
                    m.BlobHash == first && m.PipelineVersion >= BlobMetadata.CurrentPipelineVersion,
                ct
            );
        if (source is null)
        {
            return;
        }

        db.BlobMetadata.Add(
            new BlobMetadata
            {
                BlobHash = hash,
                PipelineVersion = source.PipelineVersion,
                TakenAt = source.TakenAt,
                TakenOffsetMinutes = source.TakenOffsetMinutes,
                Camera = source.Camera,
                DurationSeconds = source.DurationSeconds,
                Latitude = source.Latitude,
                Longitude = source.Longitude,
                Tags = source.Tags,
                Readable = source.Readable,
                UpdatedAt = time.GetUtcNow(),
            }
        );
    }

    private async Task<bool> CopyTextAsync(
        PdfOperation op,
        PdfPart part,
        List<PageMapping> pages,
        ContentHash hash,
        CancellationToken ct
    )
    {
        if (await db.BlobContents.AnyAsync(c => c.BlobHash == hash, ct))
        {
            return false;
        }

        var used = part.Pages.Select(r => r.Source).Distinct().ToList();
        var hashes = used.Select(s => HashOf(op.Sources[s])).ToList();
        var contents = await db
            .BlobContents.AsNoTracking()
            .Where(c =>
                hashes.Contains(c.BlobHash)
                && c.PipelineVersion >= BlobContent.CurrentPipelineVersion
            )
            .ToListAsync(ct);
        if (contents.Count != hashes.Count)
        {
            return false;
        }

        var read = await db
            .ContentPages.AsNoTracking()
            .Where(p =>
                hashes.Contains(p.BlobHash)
                && p.VersionId == null
                && (p.Source == PageSource.Text || p.Source == PageSource.Ocr)
            )
            .ToListAsync(ct);
        var byPage = read.ToDictionary(p => (p.BlobHash, p.PageNo));
        var copied = 0;
        foreach (var page in pages)
        {
            if (
                byPage.TryGetValue((HashOf(op.Sources[page.Source]), page.SourcePage), out var from)
            )
            {
                db.ContentPages.Add(
                    new ContentPage
                    {
                        BlobHash = hash,
                        PageNo = page.Page,
                        Source = from.Source,
                        Text = from.Text,
                        Words = from.Words,
                    }
                );
                copied++;
            }
        }

        db.BlobContents.Add(
            new BlobContent
            {
                BlobHash = hash,
                Status = copied > 0 ? ContentStatus.Ready : ContentStatus.Empty,
                PageCount = pages.Count,
                OcrSkipped = contents.Any(c => c.OcrSkipped),
                OcrEngine = contents.Select(c => c.OcrEngine).FirstOrDefault(e => e is not null),
                UpdatedAt = time.GetUtcNow(),
            }
        );
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// What a new file takes from its sources: the first source's details (title, description, date taken), the
    /// person's text edits on the pages it took, the tags people added, and a "Made from" line per stretch of pages.
    /// </summary>
    private async Task CarryAsync(
        Actor actor,
        PdfOperation op,
        PdfPart part,
        List<PageMapping> pages,
        AddedFile added,
        CancellationToken ct
    )
    {
        var version = added.Version;
        var used = part.Pages.Select(r => r.Source).Distinct().ToList();
        await FileDetailsService.CarryEditsAsync(
            db,
            op.Sources[used[0]].VersionId,
            version,
            withText: false,
            ct
        );

        var versionIds = used.Select(s => op.Sources[s].VersionId).ToList();
        var edits = await db
            .ContentPages.AsNoTracking()
            .Where(p =>
                p.VersionId != null
                && versionIds.Contains(p.VersionId.Value)
                && p.PageNo != ContentPage.MetadataPage
            )
            .ToListAsync(ct);
        var byPage = edits.ToDictionary(p => (p.VersionId!.Value, p.PageNo));
        foreach (var page in pages)
        {
            if (
                byPage.TryGetValue(
                    (op.Sources[page.Source].VersionId, page.SourcePage),
                    out var from
                )
            )
            {
                db.ContentPages.Add(
                    new ContentPage
                    {
                        BlobHash = version.BlobHash,
                        VersionId = version.Id,
                        PageNo = page.Page,
                        Source = from.Source,
                        Text = from.Text,
                        EditedBy = from.EditedBy,
                        EditedAt = from.EditedAt,
                    }
                );
            }
        }

        var nodeIds = used.Select(s => op.Sources[s].NodeId).ToList();
        var tags = await db
            .NodeTags.AsNoTracking()
            .Where(t => nodeIds.Contains(t.NodeId))
            .ToListAsync(ct);
        var now = time.GetUtcNow();
        foreach (var tag in tags.DistinctBy(t => t.TagKey))
        {
            db.NodeTags.Add(
                new NodeTag
                {
                    NodeId = added.Node.Id,
                    Tag = tag.Tag,
                    AddedBy = actor.UserId,
                    AddedAt = now,
                }
            );
        }

        var position = 0;
        var at = 1;
        foreach (var range in part.Pages)
        {
            var source = op.Sources[range.Source];
            db.FileOrigins.Add(
                new FileOrigin
                {
                    VersionId = version.Id,
                    Position = position++,
                    Kind = op.Kind,
                    SourceNodeId = source.NodeId,
                    SourceName = NodeName.From(source.Name),
                    SourceFirst = range.First,
                    SourceLast = range.Last,
                    First = at,
                }
            );
            at += range.Count;
        }

        await db.SaveChangesAsync(ct);
    }
}
