using Dokument.Domain.Backups;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Settings;
using Dokument.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dokument.Infrastructure.Backups;

/// <summary>
/// Database backups (docs/design.md §19): queues them on the schedule or when an admin asks, and runs them in the
/// worker. A backup is a <c>pg_dump -Fc</c> and the name manifest beside it (§7.1), written to temp files and renamed,
/// so a half-written backup never looks like a good one. After each success the oldest ones past the "keep" setting
/// are deleted.
/// </summary>
public sealed partial class BackupService(
    DokumentDbContext db,
    SettingsStore settings,
    NameManifest manifest,
    IOptions<StorageOptions> storage,
    IOptions<BackupOptions> options,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<BackupService> logger
)
{
    /// <summary>Failed backups beyond this many are forgotten, so a long outage doesn't grow the list without end.</summary>
    private const int MaxFailedKept = 30;

    private const string PartialSuffix = ".partial";

    /// <summary>A claim lasts the longest a backup may run, plus a margin, so only a dead worker's claim lapses.</summary>
    private TimeSpan Lease => options.Value.Timeout + TimeSpan.FromMinutes(10);

    public string Folder => storage.Value.BackupsPath;

    /// <summary>Queues a backup an admin asked for, or returns null when one is already queued or running.</summary>
    public async Task<Backup?> RequestAsync(Guid adminId, CancellationToken ct)
    {
        if (
            await db.Backups.AnyAsync(
                b => b.Status == BackupStatus.Queued || b.Status == BackupStatus.Running,
                ct
            )
        )
        {
            return null;
        }

        var backup = Backup.Queue(BackupTrigger.Admin, time.GetUtcNow(), requestedBy: adminId);
        db.Backups.Add(backup);
        await db.SaveChangesAsync(ct);
        return backup;
    }

    /// <summary>
    /// Queues the scheduled backup that is due, if it isn't queued yet. A time missed while the server was off is
    /// caught up when the worker starts, but only once there have been backups: a new install waits for its first
    /// scheduled time after <paramref name="workerStarted"/>. Nor does a time before the schedule was last saved, so
    /// changing the time doesn't start a backup right away, or one with a backup (say, "Back up now") after it.
    /// </summary>
    public async Task<Backup?> QueueDueAsync(DateTimeOffset workerStarted, CancellationToken ct)
    {
        var schedule = await settings.GetAsync(SettingKeys.Backups, ct);
        var now = time.GetUtcNow();
        if (schedule.LatestDue(now) is not { } due)
        {
            return null;
        }

        var savedAt = await db
            .Settings.Where(s => s.Key == SettingKeys.Backups.Name)
            .Select(s => (DateTimeOffset?)s.UpdatedAt)
            .SingleOrDefaultAsync(ct);
        if (
            savedAt > due
            || (due < workerStarted && !await db.Backups.AnyAsync(ct))
            || await db.Backups.AnyAsync(b => b.ScheduledFor == due || b.QueuedAt >= due, ct)
        )
        {
            return null;
        }

        var backup = Backup.Queue(BackupTrigger.Schedule, now, scheduledFor: due);
        db.Backups.Add(backup);
        try
        {
            await db.SaveChangesAsync(ct);
            return backup;
        }
        catch (DbUpdateException e)
            when (e.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // Another worker queued it first.
            db.Entry(backup).State = EntityState.Detached;
            return null;
        }
    }

    /// <summary>
    /// Takes the oldest queued backup, or one whose worker died, unless another is running: one backup at a time is
    /// plenty, and it keeps two dumps from competing for the disk.
    /// </summary>
    public async Task<Backup?> ClaimAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var lockedUntil = now + Lease;
        var claimed = await db
            .Backups.FromSql(
                $"""
                UPDATE backups SET status = 'running', locked_until = {lockedUntil}
                WHERE id = (
                    SELECT id FROM backups
                    WHERE (status = 'queued' OR (status = 'running' AND locked_until < {now}))
                      AND NOT EXISTS (SELECT 1 FROM backups r WHERE r.status = 'running' AND r.locked_until >= {now})
                    ORDER BY queued_at
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED)
                RETURNING *
                """
            )
            .ToListAsync(ct);
        return claimed.SingleOrDefault();
    }

    /// <summary>Starts a backup right here, for <c>dokument backup</c>, and runs it to the end.</summary>
    public async Task<Backup> RunNowAsync(CancellationToken ct)
    {
        var backup = Backup.Queue(BackupTrigger.Command, time.GetUtcNow());
        // Running from the start, so a worker doesn't claim it too.
        backup.Start(time.GetUtcNow(), Lease);
        db.Backups.Add(backup);
        await db.SaveChangesAsync(ct);
        await RunAsync(backup, ct);
        return backup;
    }

    /// <summary>
    /// Makes the dump and the manifest for a claimed backup and records how it went. Failures are recorded on the
    /// backup, not thrown. If <paramref name="ct"/> is cancelled (the worker is stopping), the backup goes back to
    /// the queue and the exception is rethrown.
    /// </summary>
    public async Task RunAsync(Backup backup, CancellationToken ct)
    {
        backup.Start(time.GetUtcNow(), Lease);
        while (await NameTakenAsync(backup, ct))
        {
            backup.TakeNextName();
        }

        await db.SaveChangesAsync(ct);
        LogStarted(backup.Name!, backup.Trigger);

        var dumpPath = Path.Combine(Folder, backup.DatabaseFile!);
        var manifestPath = Path.Combine(Folder, backup.ManifestFile!);
        var dumpPartial = dumpPath + PartialSuffix;
        var manifestPartial = manifestPath + PartialSuffix;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(options.Value.Timeout);
        try
        {
            if (Prepare() is { } problem)
            {
                await FailAsync(backup, problem.Error, problem.Detail);
                return;
            }

            if (await SpaceProblemAsync(ct) is { } space)
            {
                await FailAsync(backup, BackupError.NoSpace, space);
                return;
            }

            try
            {
                await PgDump.RunAsync(
                    options.Value.PgDumpCommand,
                    // Not the context's own: that one has the password taken out.
                    DatabaseOptions.GetConnectionString(configuration),
                    dumpPartial,
                    deadline.Token
                );
            }
            catch (PgDumpException e)
            {
                Delete(dumpPartial);
                await FailAsync(
                    backup,
                    IsDiskFull(e.Message) ? BackupError.NoSpace : BackupError.DumpFailed,
                    e.Message
                );
                return;
            }

            try
            {
                await manifest.WriteAsync(manifestPartial, deadline.Token);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Delete(dumpPartial);
                Delete(manifestPartial);
                await FailAsync(
                    backup,
                    IsDiskFull(e.Message) ? BackupError.NoSpace : BackupError.ManifestFailed,
                    e.Message
                );
                return;
            }

            File.Move(dumpPartial, dumpPath, overwrite: true);
            File.Move(manifestPartial, manifestPath, overwrite: true);
            backup.Succeed(
                time.GetUtcNow(),
                new FileInfo(dumpPath).Length,
                new FileInfo(manifestPath).Length
            );
            await db.SaveChangesAsync(CancellationToken.None);
            LogDone(backup.Name!, backup.DatabaseBytes!.Value);
            await PruneAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Delete(dumpPartial);
            Delete(manifestPartial);
            await FailAsync(
                backup,
                BackupError.TimedOut,
                $"Stopped after {options.Value.Timeout}."
            );
        }
        catch (OperationCanceledException)
        {
            Delete(dumpPartial);
            Delete(manifestPartial);
            ForgetOtherChanges(backup);
            backup.Release();
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>Whether another backup (say, two "Back up now"s in one second) already has these file names.</summary>
    private async Task<bool> NameTakenAsync(Backup backup, CancellationToken ct) =>
        File.Exists(Path.Combine(Folder, backup.DatabaseFile!))
        || File.Exists(Path.Combine(Folder, backup.ManifestFile!))
        || await db.Backups.AnyAsync(b => b.Id != backup.Id && b.Name == backup.Name, ct);

    /// <summary>
    /// Makes sure the folder exists and can be written, and removes temp files a crashed run left. Only one backup runs
    /// at a time (<see cref="ClaimAsync"/>), so every temp file there is a leftover.
    /// </summary>
    private (BackupError Error, string Detail)? Prepare()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            foreach (var leftover in Directory.EnumerateFiles(Folder, "*" + PartialSuffix))
            {
                File.Delete(leftover);
            }

            var probe = Path.Combine(Folder, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (BackupError.FolderNotWritable, $"{Folder}: {e.Message}");
        }
    }

    /// <summary>
    /// Says so when the folder's disk clearly can't hold another backup: less free space than the last one took, plus
    /// a tenth. The first backup has nothing to go by and just tries.
    /// </summary>
    private async Task<string?> SpaceProblemAsync(CancellationToken ct)
    {
        var last = await db
            .Backups.Where(b => b.Status == BackupStatus.Done)
            .OrderByDescending(b => b.FinishedAt)
            .Select(b => new { b.DatabaseBytes, b.ManifestBytes })
            .FirstOrDefaultAsync(ct);
        if (last is null)
        {
            return null;
        }

        var needed = (long)(((last.DatabaseBytes ?? 0) + (last.ManifestBytes ?? 0)) * 1.1);
        var free = FreeBytes();
        return free is { } bytes && bytes < needed
            ? $"{bytes} bytes free in {Folder}, about {needed} needed."
            : null;
    }

    /// <summary>Free space on the disk that holds the backups folder, or null when it can't be told.</summary>
    public long? FreeBytes()
    {
        try
        {
            return new DriveInfo(Path.GetFullPath(Folder)).AvailableFreeSpace;
        }
        catch (Exception e)
            when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes successful backups past the newest <see cref="BackupSettings.Keep"/>, with their files, and failed ones
    /// older than the oldest kept or past <see cref="MaxFailedKept"/>.
    /// </summary>
    public async Task PruneAsync(CancellationToken ct)
    {
        var keep = (await settings.GetAsync(SettingKeys.Backups, ct)).Keep;
        var done = await db
            .Backups.Where(b => b.Status == BackupStatus.Done)
            .OrderByDescending(b => b.FinishedAt)
            .ToListAsync(ct);
        var old = done.Skip(keep).ToList();
        foreach (var backup in old)
        {
            Delete(Path.Combine(Folder, backup.DatabaseFile!));
            Delete(Path.Combine(Folder, backup.ManifestFile!));
            LogPruned(backup.Name!);
        }

        db.Backups.RemoveRange(old);

        var oldestKept = done.Take(keep).LastOrDefault()?.FinishedAt;
        var failed = await db
            .Backups.Where(b => b.Status == BackupStatus.Failed)
            .OrderByDescending(b => b.FinishedAt)
            .ToListAsync(ct);
        db.Backups.RemoveRange(
            failed.Where(
                (b, i) => i >= MaxFailedKept || (oldestKept is { } at && b.FinishedAt < at)
            )
        );
        await db.SaveChangesAsync(ct);
    }

    private async Task FailAsync(Backup backup, BackupError error, string? detail)
    {
        ForgetOtherChanges(backup);
        backup.Fail(time.GetUtcNow(), error, detail);
        await db.SaveChangesAsync(CancellationToken.None);
        LogFailed(backup.Name!, error, detail);
    }

    private void ForgetOtherChanges(Backup backup)
    {
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.Entity != backup).ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    private static bool IsDiskFull(string message) =>
        message.Contains("No space left on device", StringComparison.OrdinalIgnoreCase)
        || message.Contains("not enough space", StringComparison.OrdinalIgnoreCase);

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup {Name} started ({Trigger})")]
    private partial void LogStarted(string name, BackupTrigger trigger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Backup {Name} done: the dump is {Bytes} bytes"
    )]
    private partial void LogDone(string name, long bytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backup {Name} failed ({Error}): {Detail}")]
    private partial void LogFailed(string name, BackupError error, string? detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted old backup {Name}")]
    private partial void LogPruned(string name);
}
