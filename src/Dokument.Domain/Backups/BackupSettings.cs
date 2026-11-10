namespace Dokument.Domain.Backups;

public enum BackupFrequency
{
    Daily,
    Weekly,
    Off,
}

/// <summary>
/// When the worker backs up the database and how many backups it keeps (docs/design.md §19). An admin picks every day
/// or every week at a time of day, in their own time zone; nobody types a cron expression.
/// </summary>
/// <param name="Weekday">The day for <see cref="BackupFrequency.Weekly"/>; ignored otherwise.</param>
/// <param name="TimeZone">
/// An IANA zone such as <c>Europe/Stockholm</c>, from the admin's browser when they saved. Null means the server's
/// own zone.
/// </param>
/// <param name="Keep">How many successful backups are kept; older ones are deleted after each new one.</param>
public sealed record BackupSettings(
    BackupFrequency Frequency,
    TimeOnly At,
    DayOfWeek Weekday,
    string? TimeZone,
    int Keep
)
{
    public const int MaxKeep = 365;

    public static readonly BackupSettings Default = new(
        BackupFrequency.Daily,
        new TimeOnly(3, 0),
        DayOfWeek.Sunday,
        null,
        14
    );

    public bool IsValid =>
        Enum.IsDefined(Frequency)
        && Enum.IsDefined(Weekday)
        && Keep is >= 1 and <= MaxKeep
        && (TimeZone is null || TryFindZone(TimeZone) is not null);

    /// <summary>The zone the time of day is in. An unknown zone (say, after moving servers) falls back to the server's.</summary>
    public TimeZoneInfo Zone =>
        (TimeZone is null ? null : TryFindZone(TimeZone)) ?? TimeZoneInfo.Local;

    /// <summary>
    /// The latest scheduled time at or before <paramref name="now"/>, at most a week back, or null when backups are off.
    /// The worker queues it once (the backups table makes it unique), so a server that was off at 03:00 backs up when
    /// it starts.
    /// </summary>
    public DateTimeOffset? LatestDue(DateTimeOffset now)
    {
        if (Frequency == BackupFrequency.Off)
        {
            return null;
        }

        var zone = Zone;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        for (var day = today; day >= today.AddDays(-7); day = day.AddDays(-1))
        {
            if (Frequency == BackupFrequency.Weekly && day.DayOfWeek != Weekday)
            {
                continue;
            }

            var at = ToInstant(day.ToDateTime(At), zone);
            if (at <= now)
            {
                return at;
            }
        }

        return null;
    }

    /// <summary>The next scheduled time after <paramref name="now"/>, for "Next backup" on the admin page.</summary>
    public DateTimeOffset? NextDue(DateTimeOffset now)
    {
        if (Frequency == BackupFrequency.Off)
        {
            return null;
        }

        var zone = Zone;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        for (var day = today; day <= today.AddDays(8); day = day.AddDays(1))
        {
            if (Frequency == BackupFrequency.Weekly && day.DayOfWeek != Weekday)
            {
                continue;
            }

            var at = ToInstant(day.ToDateTime(At), zone);
            if (at > now)
            {
                return at;
            }
        }

        return null;
    }

    /// <summary>A wall-clock time in <paramref name="zone"/>. One skipped by a clock change runs an hour later.</summary>
    private static DateTimeOffset ToInstant(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1);
        }

        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
    }

    private static TimeZoneInfo? TryFindZone(string id) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : null;
}
