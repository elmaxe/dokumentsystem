using Dokument.Domain.Files;
using Dokument.Domain.Jobs;
using Dokument.Domain.Previews;
using Dokument.Domain.Search;
using Dokument.Domain.Settings;
using Dokument.Infrastructure.Jobs;
using Dokument.Infrastructure.Ml;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;

namespace Dokument.Infrastructure.Search;

/// <summary>How far speech to text has got, for the admin page.</summary>
/// <param name="Done">Recordings transcribed, speech found or not.</param>
/// <param name="WaitingSeconds">How much sound the waiting recordings hold, for "about 61 hours".</param>
public sealed record TranscriptionProgress(
    int Done,
    int Waiting,
    double WaitingSeconds,
    int Running,
    int Failed
);

/// <summary>A blob's transcript for the viewer: its lines in order, with times.</summary>
public sealed record Transcript(BlobTranscript Info, IReadOnlyList<TranscriptLine> Lines);

/// <summary>
/// Queues speech to text (docs/design.md §11.1) and reads transcripts back. Transcripts belong to the bytes, like
/// other text. Nothing is queued while there's no ML service to do it, so jobs never pile up on a server without one;
/// adding a service queues the backlog.
/// </summary>
public sealed class TranscriptService(
    DokumentDbContext db,
    JobQueue jobs,
    SettingsStore settings,
    MlServices ml
)
{
    /// <summary>Whether a recording should be transcribed now: on, an ML service set, and sound to transcribe.</summary>
    private async Task<bool> WantedAsync(BlobMedia media, CancellationToken ct)
    {
        if (!media.HasAudio)
        {
            return false;
        }

        var transcription = await settings.GetAsync(SettingKeys.Transcription, ct);
        return transcription.Enabled
            && (!media.HasVideo || transcription.Videos)
            && await ml.ConfiguredAsync(ct);
    }

    /// <summary>Queues a recording unless it's transcribed already. Adds to the unit of work; the caller saves.</summary>
    public async Task RequestAsync(
        BlobMedia media,
        JobPriority priority,
        CancellationToken ct = default
    )
    {
        if (
            await WantedAsync(media, ct)
            && !await db.BlobTranscripts.AnyAsync(
                t => t.BlobHash == media.BlobHash && t.Status != TranscriptStatus.Partial,
                ct
            )
        )
        {
            await jobs.EnqueueAsync(JobType.Transcribe, media.BlobHash.Hex, priority, ct);
        }
    }

    /// <summary>
    /// The backfill, on worker start and when an admin adds an ML service or turns transcription on: every stored
    /// recording not transcribed yet, at background priority. Returns how many were queued.
    /// </summary>
    public async Task<int> EnqueueMissingAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var transcription = await settings.GetAsync(SettingKeys.Transcription, ct);
        if (!transcription.Enabled || !await ml.ConfiguredAsync(ct))
        {
            return 0;
        }

        var videos = transcription.Videos;
        return await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO jobs (id, type, subject, status, priority, attempts, run_after, created_at)
            SELECT gen_random_uuid(), {JobType.Transcribe.Name}, encode(m.blob_hash, 'hex'), 'queued', {(int)
                JobPriority.Background}, 0, {now}, {now}
            FROM blob_media m
            WHERE m.has_audio AND (NOT m.has_video OR {videos})
              AND EXISTS (SELECT 1 FROM blobs b WHERE b.hash = m.blob_hash AND b.ref_count > 0)
              AND NOT EXISTS (SELECT 1 FROM blob_transcripts t WHERE t.blob_hash = m.blob_hash AND t.status <> 'partial')
              AND NOT EXISTS (SELECT 1 FROM jobs j WHERE j.type = {JobType.Transcribe.Name} AND j.subject = encode(m.blob_hash, 'hex')
                              AND j.status IN ('queued', 'running', 'failed'))
            """,
            ct
        );
    }

    /// <summary>
    /// When transcription is turned off, or the videos switch is: waiting jobs that no longer should run are dropped.
    /// What's transcribed already stays searchable.
    /// </summary>
    public async Task<int> DropUnwantedAsync(CancellationToken ct = default)
    {
        var transcription = await settings.GetAsync(SettingKeys.Transcription, ct);
        if (!transcription.Enabled)
        {
            return await db
                .Jobs.Where(j => j.Type == JobType.Transcribe && j.Status == JobStatus.Queued)
                .ExecuteDeleteAsync(ct);
        }

        if (transcription.Videos)
        {
            return 0;
        }

        return await db.Database.ExecuteSqlAsync(
            $"""
            DELETE FROM jobs j USING blob_media m
            WHERE j.type = {JobType.Transcribe.Name} AND j.status = 'queued'
              AND j.subject = encode(m.blob_hash, 'hex') AND m.has_video
            """,
            ct
        );
    }

    public async Task<TranscriptionProgress> ProgressAsync(CancellationToken ct = default)
    {
        var done = await db
            .Database.SqlQuery<int>(
                $"""
                SELECT count(*)::int AS "Value" FROM blob_transcripts t
                WHERE t.status <> 'partial'
                  AND EXISTS (SELECT 1 FROM blobs b WHERE b.hash = t.blob_hash AND b.ref_count > 0)
                """
            )
            .SingleAsync(ct);
        var waitingSeconds = await db
            .Database.SqlQuery<double>(
                $"""
                SELECT coalesce(sum(m.duration_seconds), 0)::float8 AS "Value"
                FROM jobs j JOIN blob_media m ON encode(m.blob_hash, 'hex') = j.subject
                WHERE j.type = {JobType.Transcribe.Name} AND j.status IN ('queued', 'running')
                """
            )
            .SingleAsync(ct);
        var counts = await db
            .Jobs.Where(j => j.Type == JobType.Transcribe)
            .GroupBy(j => j.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Status, g => g.Count, ct);
        return new TranscriptionProgress(
            done,
            counts.GetValueOrDefault(JobStatus.Queued),
            waitingSeconds,
            counts.GetValueOrDefault(JobStatus.Running),
            counts.GetValueOrDefault(JobStatus.Failed)
        );
    }

    /// <summary>The transcript of <paramref name="hash"/>, or null when there's none yet (or no speech).</summary>
    public async Task<Transcript?> ReadAsync(ContentHash hash, CancellationToken ct = default)
    {
        var info = await db
            .BlobTranscripts.AsNoTracking()
            .SingleOrDefaultAsync(t => t.BlobHash == hash, ct);
        if (info is null)
        {
            return null;
        }

        var pages = await db
            .ContentPages.AsNoTracking()
            .Where(p => p.BlobHash == hash && p.Source == PageSource.Transcript)
            .OrderBy(p => p.PageNo)
            .Select(p => p.Words)
            .ToListAsync(ct);
        return new Transcript(
            info,
            pages.OfType<string>().SelectMany(TranscriptLine.Deserialize).ToList()
        );
    }

    /// <summary>Whether a transcribe job for <paramref name="hash"/> is waiting or running.</summary>
    public Task<JobStatus?> JobStatusAsync(ContentHash hash, CancellationToken ct = default) =>
        db
            .Jobs.Where(j =>
                j.Type == JobType.Transcribe
                && j.Subject == hash.Hex
                && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running)
            )
            .Select(j => (JobStatus?)j.Status)
            .FirstOrDefaultAsync(ct);
}
