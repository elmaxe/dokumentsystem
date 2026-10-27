using Dokument.Application.Files;
using Dokument.Domain.Files;
using Dokument.Domain.Jobs;
using Dokument.Domain.Previews;
using Dokument.Domain.Search;
using Dokument.Domain.Settings;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;

namespace Dokument.Infrastructure.Jobs;

/// <summary>One queue on the admin Jobs page.</summary>
/// <param name="DoneLastWeek">Jobs finished in the last 7 days; older ones are pruned (§10.1).</param>
public sealed record QueueSummary(
    JobType Type,
    int Running,
    int Waiting,
    int Failed,
    int DoneLastWeek,
    bool Paused
);

/// <summary>How a file uses the content a job is about.</summary>
public enum JobFileUse
{
    /// <summary>It is the file's current version.</summary>
    Current,

    /// <summary>It is the current version of a file in the trash.</summary>
    Trashed,

    /// <summary>It is an older version of the file.</summary>
    OlderVersion,
}

/// <param name="Folders">The folders above the file, top first.</param>
public sealed record JobFile(
    Node Node,
    FileVersion Version,
    JobFileUse Use,
    string OwnerName,
    IReadOnlyList<Node> Folders
);

/// <summary>A job with the files that use its content, best match first. Empty when no file uses it any more.</summary>
public sealed record JobEntry(Job Job, IReadOnlyList<JobFile> Files);

/// <summary>Where one processing step stands for a file (§10.1).</summary>
public enum StepState
{
    /// <summary>Not queued and no results; the backfill will get to it.</summary>
    NotStarted,
    Waiting,

    /// <summary>Waiting, and an admin has paused the queue.</summary>
    Paused,
    Running,
    Done,

    /// <summary>The text step ran and the file has no text.</summary>
    NothingFound,

    /// <summary>The text step left scans and photos alone because text recognition is off.</summary>
    Skipped,

    /// <summary>The step doesn't apply to this kind of file.</summary>
    Unsupported,

    /// <summary>The file couldn't be read, e.g. corrupt or password-protected. Not a failed job (§10.1).</summary>
    Unreadable,

    /// <summary>The job gave up after its retries; an admin can retry it.</summary>
    Failed,
}

/// <param name="At">When it started (running), finished (done and the like) or gave up (failed).</param>
/// <param name="Ahead">For a waiting step: how many jobs of its type will run first.</param>
/// <param name="Job">The queued, running or failed job, if any.</param>
public sealed record ProcessingStep(
    JobType Type,
    StepState State,
    DateTimeOffset? At,
    int? Ahead,
    Job? Job
);

/// <summary>A step that is still waiting or running, for the badge in file lists.</summary>
public sealed record ActiveStep(JobType Type, bool Running);

/// <summary>
/// What the worker is doing, by file (docs/design.md §10.1): the admin Jobs page, the Processing section of a file's
/// details, and the badge in file lists. Jobs are about content (a blob hash), so they are shown as the files that use
/// that content.
/// </summary>
public sealed class JobMonitor(
    DokumentDbContext db,
    DriveService drive,
    SettingsStore settings,
    TimeProvider time
)
{
    /// <summary>Most rows the Jobs page loads at once.</summary>
    public const int MaxPage = 100;

    private static readonly TimeSpan Week = TimeSpan.FromDays(7);

    public async Task<List<QueueSummary>> QueuesAsync(CancellationToken ct = default)
    {
        var paused = await settings.GetAsync(SettingKeys.Jobs, ct);
        var since = time.GetUtcNow() - Week;
        var counts = await db
            .Jobs.AsNoTracking()
            .Where(j => j.Status != JobStatus.Done || j.FinishedAt >= since)
            .GroupBy(j => new { j.Type, j.Status })
            .Select(g => new
            {
                g.Key.Type,
                g.Key.Status,
                Count = g.Count(),
            })
            .ToListAsync(ct);
        int Count(JobType type, JobStatus status) =>
            counts.FirstOrDefault(c => c.Type == type && c.Status == status)?.Count ?? 0;
        return JobType
            .All.Select(t => new QueueSummary(
                t,
                Count(t, JobStatus.Running),
                Count(t, JobStatus.Queued),
                Count(t, JobStatus.Failed),
                Count(t, JobStatus.Done),
                paused.IsPaused(t)
            ))
            .ToList();
    }

    /// <summary>
    /// One page of jobs in <paramref name="status"/>: running ones longest-running first, waiting ones in the order they
    /// will run, failed ones latest first.
    /// </summary>
    public async Task<List<JobEntry>> ListAsync(
        JobStatus status,
        JobType? type,
        int offset,
        int limit,
        CancellationToken ct = default
    )
    {
        // Keyword-rule runs aren't about a file; the admin Tags page shows them (§13.4).
        var query = db
            .Jobs.AsNoTracking()
            .Where(j => j.Status == status && j.Type != JobType.TagRule);
        if (type is not null)
        {
            query = query.Where(j => j.Type == type);
        }

        query = status switch
        {
            JobStatus.Running => query.OrderBy(j => j.StartedAt).ThenBy(j => j.Id),
            JobStatus.Queued => query
                .OrderByDescending(j => j.Priority)
                .ThenBy(j => j.RunAfter)
                .ThenBy(j => j.Id),
            _ => query.OrderByDescending(j => j.FinishedAt).ThenBy(j => j.Id),
        };
        var jobs = await query
            .Skip(Math.Max(0, offset))
            .Take(Math.Clamp(limit, 1, MaxPage))
            .ToListAsync(ct);
        var files = await FilesAsync(jobs.Select(j => j.Subject), ct);
        return jobs.Select(j => new JobEntry(j, files.GetValueOrDefault(j.Subject, []))).ToList();
    }

    public async Task<Job?> FindAsync(Guid id, CancellationToken ct = default) =>
        await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id, ct);

    /// <summary>Runs a failed job again with a fresh set of attempts. False when there is no such failed job.</summary>
    public async Task<bool> RetryAsync(Guid id, CancellationToken ct = default)
    {
        var job = await db.Jobs.SingleOrDefaultAsync(
            j => j.Id == id && j.Status == JobStatus.Failed,
            ct
        );
        if (job is null)
        {
            return false;
        }

        job.Requeue(time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>"Retry all failed", for one type or all. Returns how many were queued again.</summary>
    public Task<int> RetryFailedAsync(JobType? type, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var failed = db.Jobs.Where(j => j.Status == JobStatus.Failed);
        if (type is not null)
        {
            failed = failed.Where(j => j.Type == type);
        }

        return failed.ExecuteUpdateAsync(
            s =>
                s.SetProperty(j => j.Status, JobStatus.Queued)
                    .SetProperty(j => j.Attempts, 0)
                    .SetProperty(j => j.RunAfter, now)
                    .SetProperty(j => j.FinishedAt, (DateTimeOffset?)null),
            ct
        );
    }

    /// <summary>Forgets a failed job without running it. False when there is no such failed job.</summary>
    public async Task<bool> ClearAsync(Guid id, CancellationToken ct = default) =>
        await db.Jobs.Where(j => j.Id == id && j.Status == JobStatus.Failed).ExecuteDeleteAsync(ct)
        > 0;

    public async Task SetPausedAsync(
        JobType type,
        bool paused,
        Actor actor,
        CancellationToken ct = default
    )
    {
        var current = await settings.GetAsync(SettingKeys.Jobs, ct);
        if (current.IsPaused(type) != paused)
        {
            await settings.SetAsync(SettingKeys.Jobs, current.With(type, paused), actor, ct);
        }
    }

    /// <summary>For a file list: the steps still waiting or running for each of these contents.</summary>
    public async Task<Dictionary<ContentHash, List<ActiveStep>>> ActiveAsync(
        IReadOnlyCollection<ContentHash> hashes,
        CancellationToken ct = default
    )
    {
        var byHex = hashes.Distinct().ToDictionary(h => h.Hex);
        if (byHex.Count == 0)
        {
            return [];
        }

        var subjects = byHex.Keys.ToList();
        var active = await db
            .Jobs.AsNoTracking()
            .Where(j =>
                subjects.Contains(j.Subject)
                && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running)
            )
            .Select(j => new
            {
                j.Type,
                j.Subject,
                j.Status,
            })
            .ToListAsync(ct);
        return active
            .GroupBy(j => byHex[j.Subject])
            .ToDictionary(
                g => g.Key,
                g =>
                    g.OrderBy(j => PipelineOrder(j.Type))
                        .Select(j => new ActiveStep(j.Type, j.Status == JobStatus.Running))
                        .ToList()
            );
    }

    /// <summary>Every step for one file's current content, in pipeline order (the details panel).</summary>
    public async Task<List<ProcessingStep>> StepsAsync(
        ContentHash hash,
        CancellationToken ct = default
    )
    {
        var jobs = await db.Jobs.AsNoTracking().Where(j => j.Subject == hash.Hex).ToListAsync(ct);
        var paused = await settings.GetAsync(SettingKeys.Jobs, ct);
        var preview = await db
            .BlobPreviews.AsNoTracking()
            .SingleOrDefaultAsync(p => p.BlobHash == hash, ct);
        var metadata = await db
            .BlobMetadata.AsNoTracking()
            .SingleOrDefaultAsync(m => m.BlobHash == hash, ct);
        var content = await db
            .BlobContents.AsNoTracking()
            .SingleOrDefaultAsync(c => c.BlobHash == hash, ct);

        var steps = new List<ProcessingStep>();
        foreach (var type in JobType.All)
        {
            // Finished jobs say nothing the results don't; a queued, running or failed one wins over them.
            var job = jobs.Where(j => j.Type == type && j.Status != JobStatus.Done)
                .OrderByDescending(j => j.CreatedAt)
                .FirstOrDefault();
            steps.Add(
                job switch
                {
                    { Status: JobStatus.Running } => new(
                        type,
                        StepState.Running,
                        job.StartedAt,
                        null,
                        job
                    ),
                    { Status: JobStatus.Queued } => new(
                        type,
                        paused.IsPaused(type) ? StepState.Paused : StepState.Waiting,
                        null,
                        await AheadAsync(job, ct),
                        job
                    ),
                    { Status: JobStatus.Failed } => new(
                        type,
                        StepState.Failed,
                        job.FinishedAt,
                        null,
                        job
                    ),
                    _ => Result(type, preview, metadata, content),
                }
            );
        }

        return steps;
    }

    private static ProcessingStep Result(
        JobType type,
        BlobPreview? preview,
        BlobMetadata? metadata,
        BlobContent? content
    )
    {
        var (state, at) = type switch
        {
            _ when type == JobType.Thumbnail => preview switch
            {
                null => (StepState.NotStarted, (DateTimeOffset?)null),
                { Status: PreviewStatus.Ready } => (StepState.Done, preview.UpdatedAt),
                { Status: PreviewStatus.Unsupported } => (StepState.Unsupported, preview.UpdatedAt),
                _ => (StepState.Unreadable, preview.UpdatedAt),
            },
            _ when type == JobType.Metadata => metadata switch
            {
                null => (StepState.NotStarted, null),
                { Readable: true } => (StepState.Done, metadata.UpdatedAt),
                _ => (StepState.Unsupported, metadata.UpdatedAt),
            },
            _ => content switch
            {
                null => (StepState.NotStarted, null),
                { OcrSkipped: true } => (StepState.Skipped, content.UpdatedAt),
                { Status: ContentStatus.Ready } => (StepState.Done, content.UpdatedAt),
                { Status: ContentStatus.Empty } => (StepState.NothingFound, content.UpdatedAt),
                { Status: ContentStatus.Unsupported } => (StepState.Unsupported, content.UpdatedAt),
                _ => (StepState.Unreadable, content.UpdatedAt),
            },
        };
        return new ProcessingStep(type, state, at, null, null);
    }

    private static int PipelineOrder(JobType type)
    {
        for (var i = 0; i < JobType.All.Count; i++)
        {
            if (JobType.All[i] == type)
            {
                return i;
            }
        }

        return JobType.All.Count;
    }

    /// <summary>How many jobs of the same type the claim query will take before this one.</summary>
    private Task<int> AheadAsync(Job job, CancellationToken ct) =>
        db.Jobs.CountAsync(
            j =>
                j.Type == job.Type
                && j.Status == JobStatus.Queued
                && j.Id != job.Id
                && (
                    j.Priority > job.Priority
                    || (j.Priority == job.Priority && j.RunAfter < job.RunAfter)
                ),
            ct
        );

    /// <summary>
    /// The files that use each content, keyed by hex hash: files whose current version it is first, then trashed ones,
    /// then files that have it as an older version.
    /// </summary>
    private async Task<Dictionary<string, List<JobFile>>> FilesAsync(
        IEnumerable<string> subjects,
        CancellationToken ct
    )
    {
        var hashes = subjects
            .Select(s => ContentHash.TryParse(s, out var hash) ? hash : null)
            .OfType<ContentHash>()
            .Distinct()
            .ToList();
        if (hashes.Count == 0)
        {
            return [];
        }

        var rows = await (
            from v in db.FileVersions.AsNoTracking()
            join n in db.Nodes.AsNoTracking() on v.NodeId equals n.Id
            join u in db.Users.AsNoTracking() on n.OwnerId equals u.Id
            where hashes.Contains(v.BlobHash)
            select new
            {
                Version = v,
                Node = n,
                Owner = u.DisplayName,
            }
        ).ToListAsync(ct);
        var files = rows.Select(r => new JobFile(
                r.Node,
                r.Version,
                r switch
                {
                    _ when r.Node.CurrentVersionId != r.Version.Id => JobFileUse.OlderVersion,
                    { Node.IsTrashed: true } => JobFileUse.Trashed,
                    _ => JobFileUse.Current,
                },
                r.Owner,
                []
            ))
            // A file with the content in several versions (e.g. one restored) is listed once.
            .GroupBy(f => (f.Node.Id, f.Version.BlobHash))
            .Select(g => g.OrderBy(f => f.Use).ThenByDescending(f => f.Version.Number).First())
            .ToList();
        var folders = await drive.AncestorsAsync(
            files.Select(f => f.Node).DistinctBy(n => n.Id).ToList(),
            ct
        );
        return files
            .Select(f => f with { Folders = folders[f.Node.Id] })
            .GroupBy(f => f.Version.BlobHash.Hex)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(f => f.Use).ThenBy(f => f.Node.CreatedAt).ToList()
            );
    }
}
