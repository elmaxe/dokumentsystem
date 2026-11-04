using Dokument.Domain.Files;
using Dokument.Domain.Jobs;
using Dokument.Domain.Previews;
using Dokument.Infrastructure.Jobs;
using Dokument.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dokument.Infrastructure.Previews;

/// <summary>How many playable copies exist and the disk they take, how many are still to make, and how many failed.</summary>
public sealed record CopyStats(int Count, long Bytes, int Waiting, int Failed);

/// <summary>
/// Video and audio (docs/design.md §10.3, §14): what the API tells clients about a file's media, and the playable
/// copies the transcode stage makes of files browsers can't play as they are.
/// </summary>
public sealed class MediaService(DokumentDbContext db, JobQueue jobs, DerivedStore derived)
{
    /// <summary>
    /// For a listing or the viewer: duration, streams, the copy's state and the seek previews of each media blob.
    /// Others are left out.
    /// </summary>
    public async Task<Dictionary<ContentHash, MediaState>> StatesAsync(
        IReadOnlyCollection<ContentHash> hashes,
        CancellationToken ct = default
    )
    {
        var distinct = hashes.Distinct().ToList();
        if (distinct.Count == 0)
        {
            return [];
        }

        var media = await db
            .BlobMedia.AsNoTracking()
            .Where(m => distinct.Contains(m.BlobHash))
            .ToListAsync(ct);
        if (media.Count == 0)
        {
            return [];
        }

        var subjects = media.Where(m => m.NeedsCopy).Select(m => m.BlobHash.Hex).ToList();
        var active = await db
            .Jobs.AsNoTracking()
            .Where(j =>
                j.Type == JobType.Transcode
                && subjects.Contains(j.Subject)
                && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running)
            )
            .Select(j => new { j.Subject, j.Status })
            .ToListAsync(ct);
        var jobStatus = active.ToDictionary(j => j.Subject, j => j.Status);
        var videos = media.Where(m => m.HasVideo).Select(m => m.BlobHash).ToList();
        var sheets =
            videos.Count == 0
                ? []
                : await db
                    .Renditions.AsNoTracking()
                    .Where(r => r.Kind == RenditionKind.SeekPreviews && videos.Contains(r.BlobHash))
                    .ToDictionaryAsync(r => r.BlobHash, r => (r.Width, r.Height), ct);
        return media.ToDictionary(
            m => m.BlobHash,
            m => new MediaState(
                m.DurationSeconds,
                m.HasVideo,
                m.HasAudio,
                m.VideoCodec,
                CopyStateOf(m, jobStatus.TryGetValue(m.BlobHash.Hex, out var s) ? s : null),
                m.CopyProgress,
                m.HasCover,
                m.HasWaveform,
                sheets.TryGetValue(m.BlobHash, out var sheet)
                    ? SeekPreviewSheet.From(m.DurationSeconds, sheet.Width, sheet.Height)
                    : null
            )
        );
    }

    private static CopyState CopyStateOf(BlobMedia media, JobStatus? job) =>
        !media.NeedsCopy ? CopyState.NotNeeded
        : media.CopyStatus == CopyStatus.Ready ? CopyState.Ready
        : job == JobStatus.Running ? CopyState.Making
        : job == JobStatus.Queued ? CopyState.Waiting
        : media.CopyStatus == CopyStatus.Failed ? CopyState.Failed
        : CopyState.None;

    /// <summary>
    /// Queues a playable copy of <paramref name="hash"/> if it needs one and has none. Adds to the unit of work; the
    /// caller saves. Returns whether a copy is now on its way or ready.
    /// </summary>
    public async Task<bool> RequestCopyAsync(
        ContentHash hash,
        JobPriority priority,
        CancellationToken ct = default
    )
    {
        var media = await db
            .BlobMedia.AsNoTracking()
            .SingleOrDefaultAsync(m => m.BlobHash == hash, ct);
        if (media is null || !media.NeedsCopy || media.CopyStatus == CopyStatus.Failed)
        {
            return false;
        }

        if (media.CopyStatus != CopyStatus.Ready)
        {
            // Someone is waiting to watch it: a copy queued in the background moves ahead of the rest.
            await db
                .Jobs.Where(j =>
                    j.Type == JobType.Transcode
                    && j.Subject == hash.Hex
                    && j.Status == JobStatus.Queued
                    && j.Priority < priority
                )
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Priority, priority), ct);
            await jobs.EnqueueAsync(JobType.Transcode, hash.Hex, priority, ct);
        }

        return true;
    }

    /// <summary>
    /// Queues copies for every media blob that needs one and has none, at background priority: after copies are
    /// turned on, and when the worker starts. Failed ones are left for an admin. Returns how many were queued.
    /// </summary>
    public Task<int> EnqueueMissingCopiesAsync(
        DateTimeOffset now,
        CancellationToken ct = default
    ) =>
        db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO jobs (id, type, subject, status, priority, attempts, run_after, created_at)
            SELECT gen_random_uuid(), {JobType.Transcode.Name}, encode(m.blob_hash, 'hex'), 'queued', {(int)
                JobPriority.Background}, 0, {now}, {now}
            FROM blob_media m
            WHERE m.plan <> 'direct' AND m.copy_status = 'none'
              AND EXISTS (SELECT 1 FROM blobs b WHERE b.hash = m.blob_hash AND b.ref_count > 0)
              AND NOT EXISTS (SELECT 1 FROM jobs j WHERE j.type = {JobType.Transcode.Name} AND j.subject = encode(m.blob_hash, 'hex')
                              AND j.status IN ('queued', 'running', 'failed'))
            """,
            ct
        );

    /// <summary>
    /// Queues seek previews (§14) for every video of 10 s or more that has none, at background priority, when the
    /// worker starts. Failed ones are left for an admin. Returns how many were queued.
    /// </summary>
    public Task<int> EnqueueMissingSeekPreviewsAsync(
        DateTimeOffset now,
        CancellationToken ct = default
    ) =>
        db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO jobs (id, type, subject, status, priority, attempts, run_after, created_at)
            SELECT gen_random_uuid(), {JobType.SeekPreviews.Name}, encode(m.blob_hash, 'hex'), 'queued', {(int)
                JobPriority.Background}, 0, {now}, {now}
            FROM blob_media m
            WHERE m.has_video AND m.duration_seconds >= {SeekPreviewLayout.MinDuration.TotalSeconds}
              AND EXISTS (SELECT 1 FROM blobs b WHERE b.hash = m.blob_hash AND b.ref_count > 0)
              AND NOT EXISTS (SELECT 1 FROM renditions r WHERE r.blob_hash = m.blob_hash AND r.kind = {RenditionKind.SeekPreviews.Key()})
              AND NOT EXISTS (SELECT 1 FROM jobs j WHERE j.type = {JobType.SeekPreviews.Name} AND j.subject = encode(m.blob_hash, 'hex')
                              AND j.status IN ('queued', 'running', 'failed'))
            """,
            ct
        );

    public async Task<CopyStats> CopyStatsAsync(CancellationToken ct = default)
    {
        var keys = await db
            .Renditions.AsNoTracking()
            .Where(r => r.Kind == RenditionKind.Playable)
            .Select(r => r.StorageKey)
            .ToListAsync(ct);
        long bytes = 0;
        foreach (var key in keys)
        {
            var file = new FileInfo(derived.PathOf(key));
            bytes += file.Exists ? file.Length : 0;
        }

        var waiting = await db.Jobs.CountAsync(
            j =>
                j.Type == JobType.Transcode
                && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running),
            ct
        );
        var failed = await db.BlobMedia.CountAsync(m => m.CopyStatus == CopyStatus.Failed, ct);
        return new CopyStats(keys.Count, bytes, waiting, failed);
    }

    /// <summary>
    /// Deletes every playable copy and the copy jobs still waiting, e.g. after copies are turned off. The files they
    /// were made from are untouched. Returns how many copies were deleted.
    /// </summary>
    public async Task<int> DeleteCopiesAsync(CancellationToken ct = default)
    {
        await db
            .Jobs.Where(j => j.Type == JobType.Transcode && j.Status != JobStatus.Running)
            .ExecuteDeleteAsync(ct);
        var copies = await db
            .Renditions.Where(r => r.Kind == RenditionKind.Playable)
            .ToListAsync(ct);
        db.Renditions.RemoveRange(copies);
        await db
            .BlobMedia.Where(m => m.CopyStatus != CopyStatus.None)
            .ExecuteUpdateAsync(
                s =>
                    s.SetProperty(m => m.CopyStatus, CopyStatus.None)
                        .SetProperty(m => m.CopyError, (string?)null),
                ct
            );
        await db.SaveChangesAsync(ct);
        foreach (var copy in copies)
        {
            derived.Delete(copy.StorageKey);
        }

        return copies.Count;
    }
}
