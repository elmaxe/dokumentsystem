using System.Diagnostics;
using Dokument.Domain.Jobs;
using Dokument.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dokument.Infrastructure.Jobs;

/// <summary>
/// The Postgres-backed job queue (docs/design.md §10.1). Workers claim jobs with <c>FOR UPDATE SKIP LOCKED</c>, so
/// several can share a queue without Redis. A claim is a lease: if the worker dies, the job is claimed again once
/// <see cref="Job.LockedUntil"/> has passed.
/// </summary>
public sealed class JobQueue(DokumentDbContext db, TimeProvider time)
{
    /// <summary>
    /// Queues the work unless the same job is already waiting or running. With <paramref name="evenIfRunning"/>, a
    /// running one doesn't count, for work whose running job may have read the state before the change that queues it
    /// again. Adds to the unit of work; the caller saves.
    /// </summary>
    public async Task EnqueueAsync(
        JobType type,
        string subject,
        JobPriority priority,
        CancellationToken ct = default,
        bool evenIfRunning = false
    )
    {
        var active =
            db.Jobs.Local.Any(j =>
                j.Type == type
                && j.Subject == subject
                && (evenIfRunning ? j.Status == JobStatus.Queued : j.IsActive)
            )
            || await db.Jobs.AnyAsync(
                j =>
                    j.Type == type
                    && j.Subject == subject
                    && (
                        j.Status == JobStatus.Queued
                        || (!evenIfRunning && j.Status == JobStatus.Running)
                    ),
                ct
            );
        if (!active)
        {
            // The current trace, if any, so the job's run can link back to it.
            var traceParent = Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity
                ? activity.Id
                : null;
            db.Jobs.Add(Job.Create(type, subject, priority, time.GetUtcNow(), traceParent));
        }
    }

    /// <summary>Takes the most urgent due job of <paramref name="type"/>, or returns null when there is none.</summary>
    public async Task<Job?> ClaimAsync(
        JobType type,
        string workerId,
        TimeSpan lease,
        CancellationToken ct = default
    )
    {
        var now = time.GetUtcNow();
        var lockedUntil = now + lease;
        var jobs = await db
            .Jobs.FromSql(
                $"""
                UPDATE jobs SET status = 'running', attempts = attempts + 1, locked_by = {workerId}, locked_until = {lockedUntil}
                WHERE id = (
                    SELECT id FROM jobs
                    WHERE type = {type.Name}
                      AND ((status = 'queued' AND run_after <= {now}) OR (status = 'running' AND locked_until < {now}))
                    ORDER BY priority DESC, run_after
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED)
                RETURNING *
                """
            )
            .ToListAsync(ct);
        return jobs.SingleOrDefault();
    }

    public async Task SucceedAsync(Job job, CancellationToken ct = default)
    {
        job.Succeed(time.GetUtcNow());
        await db.SaveChangesAsync(ct);
    }

    public async Task FailAsync(
        Job job,
        string error,
        RetryPolicy retry,
        CancellationToken ct = default
    )
    {
        ForgetOtherChanges(job);
        job.Fail(error, retry, time.GetUtcNow());
        await db.SaveChangesAsync(ct);
    }

    public async Task ReleaseAsync(Job job, CancellationToken ct = default)
    {
        ForgetOtherChanges(job);
        job.Release();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Drops what a failed handler left unsaved, so saving the job's state doesn't run into the same error and leave
    /// the job running.
    /// </summary>
    private void ForgetOtherChanges(Job job)
    {
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.Entity != job).ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    /// <summary>Forgets finished jobs after <paramref name="age"/>. Failed ones stay until an admin retries or clears them.</summary>
    public Task<int> PruneAsync(TimeSpan age, CancellationToken ct = default)
    {
        var before = time.GetUtcNow() - age;
        return db
            .Jobs.Where(j => j.Status == JobStatus.Done && j.FinishedAt < before)
            .ExecuteDeleteAsync(ct);
    }
}
