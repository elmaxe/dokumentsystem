using System.Globalization;
using System.Text.Json.Nodes;
using Dokument.Domain.Audit;
using Dokument.Domain.Backups;
using Dokument.Infrastructure.Backups;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Settings;
using Dokument.Server.Auth;
using Dokument.Server.Files;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Dokument.Server.Admin;

/// <summary>
/// Backups on the admin Storage page (docs/design.md §19): the schedule, "Back up now", the recent backups, and the
/// banner every admin page shows after a backup failed.
/// </summary>
public static class BackupEndpoints
{
    /// <summary>How many backups the Storage page lists.</summary>
    private const int Listed = 20;

    public static RouteGroupBuilder MapBackupEndpoints(this RouteGroupBuilder admin)
    {
        admin
            .MapGet("/backups", GetBackups)
            .WithName("AdminGetBackups")
            .WithDescription("The schedule, the newest backups first, and the space they take.");
        admin
            .MapPut("/settings/backups", PutSettings)
            .WithName("AdminPutBackupSettings")
            .WithDescription(
                "Old backups past the new number to keep are deleted after the next successful backup."
            );
        admin
            .MapPost("/backups", BackUpNow)
            .WithName("AdminBackUpNow")
            .WithDescription(
                "Queues a backup; the worker starts it within seconds. 409 when one is already queued or running."
            );
        admin
            .MapPost("/backups/{id:guid}/dismiss", Dismiss)
            .WithName("AdminDismissBackup")
            .WithDescription("Hides the banner about this failed backup.");
        admin
            .MapGet("/alerts", GetAlerts)
            .WithName("AdminGetAlerts")
            .WithDescription("What the banner at the top of every admin page says, if anything.");
        return admin;
    }

    private static async Task<Ok<BackupsDto>> GetBackups(
        DokumentDbContext db,
        SettingsStore settings,
        BackupService service,
        TimeProvider time,
        CancellationToken ct
    )
    {
        var schedule = await settings.GetAsync(SettingKeys.Backups, ct);
        var recent = await ListAsync(db, db.Backups.OrderByDescending(b => b.QueuedAt), ct);
        var done = db.Backups.Where(b => b.Status == BackupStatus.Done);
        return TypedResults.Ok(
            new BackupsDto(
                SettingsDto(schedule),
                schedule.NextDue(time.GetUtcNow()),
                service.Folder,
                await done.CountAsync(ct),
                await done.SumAsync(b => (b.DatabaseBytes ?? 0) + (b.ManifestBytes ?? 0), ct),
                service.FreeBytes(),
                recent
            )
        );
    }

    private static async Task<Results<Ok<BackupSettingsDto>, ValidationProblem>> PutSettings(
        BackupSettingsDto request,
        HttpContext context,
        SettingsStore settings,
        CancellationToken ct
    )
    {
        if (
            !TimeOnly.TryParseExact(
                request.At,
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var at
            )
        )
        {
            return Invalid("at", "The time must be HH:mm, such as 03:00.");
        }

        var value = new BackupSettings(
            request.Frequency,
            at,
            request.Weekday,
            string.IsNullOrWhiteSpace(request.TimeZone) ? null : request.TimeZone,
            request.Keep
        );
        if (!value.IsValid)
        {
            return Invalid(
                "backups",
                $"Keep 1 to {BackupSettings.MaxKeep} backups, in a time zone this server knows."
            );
        }

        await settings.SetAsync(SettingKeys.Backups, value, context.Actor(), ct);
        return TypedResults.Ok(SettingsDto(value));
    }

    private static async Task<Results<Accepted<BackupDto>, Conflict>> BackUpNow(
        HttpContext context,
        BackupService service,
        AuditLog audit,
        DokumentDbContext db,
        CancellationToken ct
    )
    {
        var actor = context.Actor();
        if (await service.RequestAsync(actor.UserId, ct) is not { } backup)
        {
            return TypedResults.Conflict();
        }

        audit.Add(
            actor,
            AuditActions.BackupRequest,
            details: new JsonObject { ["backup"] = backup.Id }
        );
        await db.SaveChangesAsync(ct);
        var name = await db
            .Users.Where(u => u.Id == actor.UserId)
            .Select(u => u.DisplayName)
            .SingleOrDefaultAsync(ct);
        return TypedResults.Accepted((string?)null, Dto(backup, name));
    }

    private static async Task<Results<NoContent, NotFound>> Dismiss(
        Guid id,
        DokumentDbContext db,
        CancellationToken ct
    )
    {
        var backup = await db.Backups.SingleOrDefaultAsync(b => b.Id == id, ct);
        if (backup is null)
        {
            return TypedResults.NotFound();
        }

        backup.Dismiss();
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// The banner shows while the latest finished backup failed and nobody dismissed it; a later success clears it by
    /// itself. It also says when the last good backup was.
    /// </summary>
    private static async Task<Ok<AdminAlertsDto>> GetAlerts(
        DokumentDbContext db,
        CancellationToken ct
    )
    {
        var latest = await ListAsync(
            db,
            db.Backups.Where(b => b.FinishedAt != null).OrderByDescending(b => b.FinishedAt),
            ct,
            take: 1
        );
        BackupAlertDto? alert = null;
        if (latest is [{ Status: BackupStatus.Failed, Dismissed: false } failed])
        {
            var lastGood = await db
                .Backups.Where(b => b.Status == BackupStatus.Done)
                .MaxAsync(b => b.FinishedAt, ct);
            alert = new BackupAlertDto(failed, lastGood);
        }

        return TypedResults.Ok(new AdminAlertsDto(alert));
    }

    private static async Task<List<BackupDto>> ListAsync(
        DokumentDbContext db,
        IQueryable<Backup> query,
        CancellationToken ct,
        int take = Listed
    )
    {
        var rows = await query
            .Take(take)
            .GroupJoin(db.Users, b => b.RequestedBy, u => u.Id, (b, users) => new { b, users })
            .SelectMany(
                x => x.users.DefaultIfEmpty(),
                (x, u) => new { Backup = x.b, Name = u == null ? null : u.DisplayName }
            )
            .AsNoTracking()
            .ToListAsync(ct);
        return rows.Select(r => Dto(r.Backup, r.Name)).ToList();
    }

    private static BackupDto Dto(Backup b, string? requestedBy) =>
        new(
            b.Id,
            b.Status,
            b.Trigger,
            requestedBy,
            b.QueuedAt,
            b.StartedAt,
            b.FinishedAt,
            b.DatabaseFile,
            b.DatabaseBytes,
            b.ManifestBytes,
            b.Error,
            b.ErrorDetail,
            b.Dismissed
        );

    private static BackupSettingsDto SettingsDto(BackupSettings s) =>
        new(
            s.Frequency,
            s.At.ToString("HH:mm", CultureInfo.InvariantCulture),
            s.Weekday,
            s.TimeZone,
            s.Keep
        );

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}

/// <param name="Frequency">daily, weekly or off. Admins can still back up by hand when it's off.</param>
/// <param name="At">The time of day, <c>HH:mm</c>, in <paramref name="TimeZone"/>.</param>
/// <param name="Weekday">The day for weekly backups.</param>
/// <param name="TimeZone">An IANA zone such as <c>Europe/Stockholm</c>; the web app sends the browser's. Null means the server's.</param>
/// <param name="Keep">How many successful backups to keep, 1 to 365.</param>
public sealed record BackupSettingsDto(
    BackupFrequency Frequency,
    string At,
    DayOfWeek Weekday,
    string? TimeZone,
    int Keep
);

/// <param name="NextAt">When the next scheduled backup starts; null when the schedule is off.</param>
/// <param name="Folder">Where the backups are, inside the containers.</param>
/// <param name="Count">How many successful backups there are.</param>
/// <param name="TotalBytes">The space they take: dumps and manifests.</param>
/// <param name="FreeBytes">Free space on the disk the folder is on, when the server can tell.</param>
/// <param name="Backups">The newest first, at most 20, failed and running ones included.</param>
public sealed record BackupsDto(
    BackupSettingsDto Settings,
    DateTimeOffset? NextAt,
    string Folder,
    int Count,
    long TotalBytes,
    long? FreeBytes,
    List<BackupDto> Backups
);

/// <param name="RequestedBy">The admin who clicked "Back up now", for those.</param>
/// <param name="DatabaseFile">The dump's file name in the folder, once started. The manifest is <c>manifest-…jsonl.gz</c> beside it.</param>
/// <param name="ErrorDetail">What went wrong, in the tool's own words (English).</param>
public sealed record BackupDto(
    Guid Id,
    BackupStatus Status,
    BackupTrigger Trigger,
    string? RequestedBy,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? DatabaseFile,
    long? DatabaseBytes,
    long? ManifestBytes,
    BackupError? Error,
    string? ErrorDetail,
    bool Dismissed
);

/// <param name="Backup">Set while the latest backup failed and nobody dismissed it.</param>
public sealed record AdminAlertsDto(BackupAlertDto? Backup);

/// <param name="LastGoodAt">When the last successful backup finished; null if there never was one.</param>
public sealed record BackupAlertDto(BackupDto Failed, DateTimeOffset? LastGoodAt);
