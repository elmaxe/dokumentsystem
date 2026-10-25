namespace Dokument.Domain.Jobs;

public enum JobStatus
{
    Queued,
    Running,
    Done,
    Failed,
}

/// <summary>
/// One unit of background work in the Postgres queue (docs/design.md §10.1). <see cref="Subject"/> names what the job
/// is about, such as a blob's hash, so the same work isn't queued twice. Jobs are idempotent: running one again
/// overwrites its outputs.
/// </summary>
public sealed class Job
{
    /// <summary>The longest an error message is kept; stack traces belong in the logs.</summary>
    public const int MaxErrorLength = 2000;

    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required JobType Type { get; init; }
    public required string Subject { get; init; }
    public JobStatus Status { get; private set; } = JobStatus.Queued;
    public JobPriority Priority { get; init; }
    public int Attempts { get; private set; }
    public DateTimeOffset RunAfter { get; private set; }
    public string? LockedBy { get; private set; }

    /// <summary>A running job whose lease has passed is taken to have died with its worker and is claimed again.</summary>
    public DateTimeOffset? LockedUntil { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the current or last attempt was claimed, for "running for 0:42" on the admin Jobs page.</summary>
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>
    /// The W3C <c>traceparent</c> of whatever queued the job, so its run links back to e.g. the upload that caused it
    /// (docs/design.md §22.1). Null when nothing was being traced.
    /// </summary>
    public string? TraceParent { get; init; }

    public bool IsActive => Status is JobStatus.Queued or JobStatus.Running;

    public static Job Create(
        JobType type,
        string subject,
        JobPriority priority,
        DateTimeOffset now,
        string? traceParent = null
    ) =>
        new()
        {
            Type = type,
            Subject = subject,
            Priority = priority,
            CreatedAt = now,
            RunAfter = now,
            TraceParent = traceParent,
        };

    public void Succeed(DateTimeOffset now)
    {
        Status = JobStatus.Done;
        FinishedAt = now;
        LockedBy = null;
        LockedUntil = null;
        LastError = null;
    }

    /// <summary>Records the error and queues the job again per <paramref name="retry"/>, or marks it failed for good.</summary>
    public void Fail(string error, RetryPolicy retry, DateTimeOffset now)
    {
        LastError = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
        LockedBy = null;
        LockedUntil = null;
        if (retry.NextAttempt(Attempts, now) is { } next)
        {
            Status = JobStatus.Queued;
            RunAfter = next;
        }
        else
        {
            Status = JobStatus.Failed;
            FinishedAt = now;
        }
    }

    /// <summary>Hands a claimed job back untouched, e.g. when the worker shuts down mid-job. The attempt doesn't count.</summary>
    public void Release()
    {
        Status = JobStatus.Queued;
        Attempts = Math.Max(0, Attempts - 1);
        LockedBy = null;
        LockedUntil = null;
    }

    /// <summary>"Retry failed" in the admin UI: a fresh set of attempts, starting now.</summary>
    public void Requeue(DateTimeOffset now)
    {
        Status = JobStatus.Queued;
        Attempts = 0;
        RunAfter = now;
        FinishedAt = null;
    }
}
