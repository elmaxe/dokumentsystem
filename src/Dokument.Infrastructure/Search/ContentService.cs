using Dokument.Domain.Files;
using Dokument.Domain.Jobs;
using Dokument.Domain.Search;
using Dokument.Infrastructure.Jobs;
using Dokument.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dokument.Infrastructure.Search;

/// <summary>How far the worker has got with reading text, for the admin page.</summary>
/// <param name="Read">Stored files whose text has been read with the current pipeline, text found or not.</param>
/// <param name="Waiting">Queued text jobs.</param>
/// <param name="Running">Text jobs a worker is on now.</param>
/// <param name="Failed">Text jobs that gave up after their retries.</param>
public sealed record TextProgress(int Read, int Waiting, int Running, int Failed);

/// <summary>
/// Queues the metadata and text stages for new content (docs/design.md §10.2) and reports their progress. Like
/// thumbnails, both belong to blobs, so identical bytes are read once.
/// </summary>
public sealed class ContentService(DokumentDbContext db, JobQueue jobs)
{
    /// <summary>Queues metadata and text for <paramref name="hash"/> unless they are already read. Adds to the unit of work; the caller saves.</summary>
    public async Task RequestAsync(
        ContentHash hash,
        JobPriority priority,
        CancellationToken ct = default
    )
    {
        if (
            !await db.BlobMetadata.AnyAsync(
                m => m.BlobHash == hash && m.PipelineVersion >= BlobMetadata.CurrentPipelineVersion,
                ct
            )
        )
        {
            await jobs.EnqueueAsync(JobType.Metadata, hash.Hex, priority, ct);
        }

        if (
            !await db.BlobContents.AnyAsync(
                c => c.BlobHash == hash && c.PipelineVersion >= BlobContent.CurrentPipelineVersion,
                ct
            )
        )
        {
            await jobs.EnqueueAsync(JobType.Text, hash.Hex, priority, ct);
        }
    }

    /// <summary>
    /// The backfill: queues both stages, at background priority, for every stored blob they haven't read with the current
    /// pipeline. Blobs whose job failed for good are left for an admin. Returns how many jobs were queued.
    /// </summary>
    public async Task<int> EnqueueMissingAsync(
        DateTimeOffset now,
        CancellationToken ct = default
    ) =>
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO jobs (id, type, subject, status, priority, attempts, run_after, created_at)
            SELECT gen_random_uuid(), {JobType.Metadata.Name}, encode(b.hash, 'hex'), 'queued', {(int)
                JobPriority.Background}, 0, {now}, {now}
            FROM (SELECT DISTINCT hash FROM blobs WHERE ref_count > 0) b
            WHERE NOT EXISTS (SELECT 1 FROM blob_metadata m WHERE m.blob_hash = b.hash AND m.pipeline_version >= {BlobMetadata.CurrentPipelineVersion})
              AND NOT EXISTS (SELECT 1 FROM jobs j WHERE j.type = {JobType.Metadata.Name} AND j.subject = encode(b.hash, 'hex')
                              AND j.status IN ('queued', 'running', 'failed'))
            """,
            ct
        )
        + await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO jobs (id, type, subject, status, priority, attempts, run_after, created_at)
            SELECT gen_random_uuid(), {JobType.Text.Name}, encode(b.hash, 'hex'), 'queued', {(int)
                JobPriority.Background}, 0, {now}, {now}
            FROM (SELECT DISTINCT hash FROM blobs WHERE ref_count > 0) b
            WHERE NOT EXISTS (SELECT 1 FROM blob_contents c WHERE c.blob_hash = b.hash AND c.pipeline_version >= {BlobContent.CurrentPipelineVersion})
              AND NOT EXISTS (SELECT 1 FROM jobs j WHERE j.type = {JobType.Text.Name} AND j.subject = encode(b.hash, 'hex')
                              AND j.status IN ('queued', 'running', 'failed'))
            """,
            ct
        );

    /// <summary>
    /// Forgets metadata and text jobs that gave up because exiftool or Tesseract wasn't installed, so the backfill queues
    /// them again. Workers since then skip a missing tool instead of failing.
    /// </summary>
    public Task<int> ForgetToolMissingAsync(CancellationToken ct = default) =>
        db.Database.ExecuteSqlAsync(
            $"""
            DELETE FROM jobs
            WHERE status = 'failed' AND type IN ({JobType.Metadata.Name}, {JobType.Text.Name})
              AND last_error LIKE 'ToolMissingException:%'
            """,
            ct
        );

    /// <summary>
    /// After text recognition is turned back on, or Tesseract is installed: reads again, in the background, every blob
    /// that had pages or images it skipped. Returns how many were queued.
    /// </summary>
    public Task<int> EnqueueSkippedAsync(DateTimeOffset now, CancellationToken ct = default) =>
        db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO jobs (id, type, subject, status, priority, attempts, run_after, created_at)
            SELECT gen_random_uuid(), {JobType.Text.Name}, encode(c.blob_hash, 'hex'), 'queued', {(int)
                JobPriority.Background}, 0, {now}, {now}
            FROM blob_contents c
            WHERE c.ocr_skipped
              AND EXISTS (SELECT 1 FROM blobs b WHERE b.hash = c.blob_hash AND b.ref_count > 0)
              AND NOT EXISTS (SELECT 1 FROM jobs j WHERE j.type = {JobType.Text.Name} AND j.subject = encode(c.blob_hash, 'hex')
                              AND j.status IN ('queued', 'running'))
            """,
            ct
        );

    /// <summary>"Read all files again" on the admin page: every stored blob, in the background. Returns how many were queued.</summary>
    public Task<int> EnqueueAllAsync(DateTimeOffset now, CancellationToken ct = default) =>
        db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO jobs (id, type, subject, status, priority, attempts, run_after, created_at)
            SELECT gen_random_uuid(), {JobType.Text.Name}, encode(b.hash, 'hex'), 'queued', {(int)
                JobPriority.Background}, 0, {now}, {now}
            FROM (SELECT DISTINCT hash FROM blobs WHERE ref_count > 0) b
            WHERE NOT EXISTS (SELECT 1 FROM jobs j WHERE j.type = {JobType.Text.Name} AND j.subject = encode(b.hash, 'hex')
                              AND j.status IN ('queued', 'running'))
            """,
            ct
        );

    public async Task<TextProgress> ProgressAsync(CancellationToken ct = default)
    {
        var read = await db
            .Database.SqlQuery<int>(
                $"""
                SELECT count(*)::int AS "Value" FROM blob_contents c
                WHERE c.pipeline_version >= {BlobContent.CurrentPipelineVersion}
                  AND EXISTS (SELECT 1 FROM blobs b WHERE b.hash = c.blob_hash AND b.ref_count > 0)
                """
            )
            .SingleAsync(ct);
        var counts = await db
            .Jobs.Where(j => j.Type == JobType.Text)
            .GroupBy(j => j.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Status, g => g.Count, ct);
        return new TextProgress(
            read,
            counts.GetValueOrDefault(JobStatus.Queued),
            counts.GetValueOrDefault(JobStatus.Running),
            counts.GetValueOrDefault(JobStatus.Failed)
        );
    }
}
