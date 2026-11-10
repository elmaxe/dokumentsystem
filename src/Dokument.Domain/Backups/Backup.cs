namespace Dokument.Domain.Backups;

public enum BackupStatus
{
    Queued,
    Running,
    Done,
    Failed,
}

/// <summary>What started a backup: its schedule, an admin's "Back up now", or <c>dokument backup</c> (docs/design.md §19).</summary>
public enum BackupTrigger
{
    Schedule,
    Admin,
    Command,
}

/// <summary>Why a backup failed, so the admin page can say it in the reader's language. The details hold the rest.</summary>
public enum BackupError
{
    /// <summary>Not enough free space in the backups folder for the dump.</summary>
    NoSpace,

    /// <summary>The backups folder is missing or can't be written to.</summary>
    FolderNotWritable,

    /// <summary><c>pg_dump</c> is missing, or it exited with an error.</summary>
    DumpFailed,

    /// <summary>The name manifest couldn't be written. The dump is deleted with it, so a backup is always both.</summary>
    ManifestFailed,

    /// <summary>It ran longer than allowed and was stopped.</summary>
    TimedOut,
}

/// <summary>
/// One backup (docs/design.md §19): a <c>pg_dump</c> of the database and the name manifest beside it (§7.1), both in
/// the backups folder and named after <see cref="Name"/>. The table is also the backups' queue: the worker claims a
/// queued row, like a job, so "Back up now" and the schedule need nothing else.
/// </summary>
public sealed class Backup
{
    /// <summary>The longest error detail kept, such as the end of what <c>pg_dump</c> printed.</summary>
    public const int MaxDetailLength = 2000;

    public Guid Id { get; init; } = Guid.CreateVersion7();
    public BackupTrigger Trigger { get; init; }

    /// <summary>The admin who asked for it, for <see cref="BackupTrigger.Admin"/>.</summary>
    public Guid? RequestedBy { get; init; }

    /// <summary>
    /// The scheduled time this backup is for. Unique, so two workers that both see a backup is due queue it once.
    /// </summary>
    public DateTimeOffset? ScheduledFor { get; init; }
    public BackupStatus Status { get; private set; } = BackupStatus.Queued;
    public DateTimeOffset QueuedAt { get; init; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>While running: when the worker's claim lapses, so a backup whose worker died is taken over.</summary>
    public DateTimeOffset? LockedUntil { get; private set; }

    /// <summary>The time stamp both files are named by: <c>dokument-{Name}.dump</c> and <c>manifest-{Name}.jsonl.gz</c>.</summary>
    public string? Name { get; private set; }
    public long? DatabaseBytes { get; private set; }
    public long? ManifestBytes { get; private set; }
    public BackupError? Error { get; private set; }
    public string? ErrorDetail { get; private set; }

    /// <summary>An admin closed the banner about this failed backup.</summary>
    public bool Dismissed { get; private set; }

    public bool IsActive => Status is BackupStatus.Queued or BackupStatus.Running;

    public string? DatabaseFile => Name is null ? null : $"dokument-{Name}.dump";
    public string? ManifestFile => Name is null ? null : $"manifest-{Name}.jsonl.gz";

    public static Backup Queue(
        BackupTrigger trigger,
        DateTimeOffset now,
        Guid? requestedBy = null,
        DateTimeOffset? scheduledFor = null
    ) =>
        new()
        {
            Trigger = trigger,
            RequestedBy = requestedBy,
            ScheduledFor = scheduledFor,
            QueuedAt = now,
        };

    /// <summary>Starts (or, after a worker died, restarts) the backup, naming its files after the start time.</summary>
    public void Start(DateTimeOffset now, TimeSpan lease)
    {
        Status = BackupStatus.Running;
        StartedAt = now;
        LockedUntil = now + lease;
        Name = NameFor(now);
        DatabaseBytes = null;
        ManifestBytes = null;
        Error = null;
        ErrorDetail = null;
    }

    public void Succeed(DateTimeOffset now, long databaseBytes, long manifestBytes)
    {
        Status = BackupStatus.Done;
        FinishedAt = now;
        LockedUntil = null;
        DatabaseBytes = databaseBytes;
        ManifestBytes = manifestBytes;
    }

    public void Fail(DateTimeOffset now, BackupError error, string? detail)
    {
        Status = BackupStatus.Failed;
        FinishedAt = now;
        LockedUntil = null;
        Error = error;
        ErrorDetail = detail is { Length: > MaxDetailLength } ? detail[..MaxDetailLength] : detail;
    }

    /// <summary>Hands a started backup back to the queue, e.g. when the worker shuts down; it starts over later.</summary>
    public void Release()
    {
        Status = BackupStatus.Queued;
        LockedUntil = null;
    }

    public void Dismiss() => Dismissed = true;

    /// <summary>Names the files a second later, when another backup started in the same second has the name.</summary>
    public void TakeNextName() =>
        Name = NameFor(
            DateTimeOffset
                .ParseExact(
                    Name!,
                    "yyyyMMdd-HHmmss",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal
                )
                .AddSeconds(1)
        );

    /// <summary>UTC, sortable, and safe in a file name on any system: <c>20261110-030000</c>.</summary>
    public static string NameFor(DateTimeOffset at) =>
        at.UtcDateTime.ToString(
            "yyyyMMdd-HHmmss",
            System.Globalization.CultureInfo.InvariantCulture
        );
}
