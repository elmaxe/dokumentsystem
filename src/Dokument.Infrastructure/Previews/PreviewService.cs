using Dokument.Domain.Files;
using Dokument.Domain.Jobs;
using Dokument.Domain.Previews;
using Dokument.Infrastructure.Jobs;
using Dokument.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dokument.Infrastructure.Previews;

/// <summary>
/// Thumbnails and previews (docs/design.md §10, §14): queues the thumbnail stage for new content, and tells the API
/// what each file has. Renditions belong to blobs, so identical bytes are only ever rendered once.
/// </summary>
public sealed class PreviewService(DokumentDbContext db, JobQueue jobs, DerivedStore derived)
{
    /// <summary>Queues thumbnails for <paramref name="hash"/> unless they are already made. Adds to the unit of work; the caller saves.</summary>
    public async Task RequestAsync(
        ContentHash hash,
        JobPriority priority,
        CancellationToken ct = default
    )
    {
        var done = await db.BlobPreviews.AnyAsync(
            p => p.BlobHash == hash && p.PipelineVersion >= BlobPreview.CurrentPipelineVersion,
            ct
        );
        if (!done)
        {
            await jobs.EnqueueAsync(JobType.Thumbnail, hash.Hex, priority, ct);
        }
    }

    /// <summary>
    /// The backfill: queues every stored blob that has no thumbnails from the current pipeline, at background priority.
    /// Covers files from before this stage existed and pipeline upgrades. Blobs whose job failed for good are left for an
    /// admin to retry. Returns how many were queued.
    /// </summary>
    public Task<int> EnqueueMissingAsync(DateTimeOffset now, CancellationToken ct = default) =>
        db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO jobs (id, type, subject, status, priority, attempts, run_after, created_at)
            SELECT gen_random_uuid(), {JobType.Thumbnail.Name}, encode(b.hash, 'hex'), 'queued', {(int)
                JobPriority.Background}, 0, {now}, {now}
            FROM (SELECT DISTINCT hash FROM blobs WHERE ref_count > 0) b
            WHERE NOT EXISTS (SELECT 1 FROM blob_previews p WHERE p.blob_hash = b.hash AND p.pipeline_version >= {BlobPreview.CurrentPipelineVersion})
              AND NOT EXISTS (SELECT 1 FROM jobs j WHERE j.type = {JobType.Thumbnail.Name} AND j.subject = encode(b.hash, 'hex')
                              AND j.status IN ('queued', 'running', 'failed'))
            """,
            ct
        );

    /// <summary>
    /// After Office previews are turned on: queues the Office and OpenDocument files recorded as unsupported while
    /// they were off, at background priority. Returns how many were queued.
    /// </summary>
    public Task<int> EnqueueOfficeAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var types = OfficeFormats.MimeTypes.Select(t => t.ToLowerInvariant()).ToArray();
        return db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO jobs (id, type, subject, status, priority, attempts, run_after, created_at)
            SELECT gen_random_uuid(), {JobType.Thumbnail.Name}, encode(p.blob_hash, 'hex'), 'queued', {(int)
                JobPriority.Background}, 0, {now}, {now}
            FROM blob_previews p
            WHERE p.status = 'unsupported'
              AND EXISTS (SELECT 1 FROM blobs b WHERE b.hash = p.blob_hash AND b.ref_count > 0)
              AND EXISTS (SELECT 1 FROM file_versions v WHERE v.blob_hash = p.blob_hash AND lower(v.mime_type) = ANY({types}))
              AND NOT EXISTS (SELECT 1 FROM jobs j WHERE j.type = {JobType.Thumbnail.Name} AND j.subject = encode(p.blob_hash, 'hex')
                              AND j.status IN ('queued', 'running'))
            """,
            ct
        );
    }

    /// <summary>Which of these blobs the viewer shows as a PDF made of them (Office documents).</summary>
    public async Task<HashSet<ContentHash>> DocumentsAsync(
        IReadOnlyCollection<ContentHash> hashes,
        CancellationToken ct = default
    )
    {
        var distinct = hashes.Distinct().ToList();
        return distinct.Count == 0
            ? []
            : (
                await db
                    .Renditions.AsNoTracking()
                    .Where(r => distinct.Contains(r.BlobHash) && r.Kind == RenditionKind.Document)
                    .Select(r => r.BlobHash)
                    .ToListAsync(ct)
            ).ToHashSet();
    }

    /// <summary>For a folder listing: which of these blobs have a thumbnail, and which are still being worked on.</summary>
    public async Task<Dictionary<ContentHash, ThumbnailState>> ThumbnailStatesAsync(
        IReadOnlyCollection<ContentHash> hashes,
        CancellationToken ct = default
    )
    {
        var distinct = hashes.Distinct().ToList();
        if (distinct.Count == 0)
        {
            return [];
        }

        var ready = (
            await db
                .BlobPreviews.AsNoTracking()
                .Where(p => distinct.Contains(p.BlobHash) && p.Status == PreviewStatus.Ready)
                .Select(p => p.BlobHash)
                .ToListAsync(ct)
        ).ToHashSet();
        var subjects = distinct.Select(h => h.Hex).ToList();
        var pending = (
            await db
                .Jobs.AsNoTracking()
                .Where(j =>
                    j.Type == JobType.Thumbnail
                    && subjects.Contains(j.Subject)
                    && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running)
                )
                .Select(j => j.Subject)
                .ToListAsync(ct)
        ).ToHashSet();
        return distinct.ToDictionary(
            h => h,
            h =>
                ready.Contains(h) ? ThumbnailState.Ready
                : pending.Contains(h.Hex) ? ThumbnailState.Pending
                : ThumbnailState.None
        );
    }

    /// <summary>The rendition's file on disk, or null if the blob has none of that kind.</summary>
    public async Task<RenditionFile?> FindAsync(
        ContentHash hash,
        RenditionKind kind,
        CancellationToken ct = default
    )
    {
        var rendition = await db
            .Renditions.AsNoTracking()
            .SingleOrDefaultAsync(r => r.BlobHash == hash && r.Kind == kind, ct);
        return rendition is null
            ? null
            : new RenditionFile(rendition, derived.PathOf(rendition.StorageKey));
    }
}

public sealed record RenditionFile(Rendition Rendition, string PhysicalPath);
