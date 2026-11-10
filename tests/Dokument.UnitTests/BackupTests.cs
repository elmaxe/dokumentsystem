using System.Text.Json;
using Dokument.Domain.Backups;
using Dokument.Infrastructure.Settings;

namespace Dokument.UnitTests;

public class BackupTests
{
    private static readonly BackupSettings Nightly = BackupSettings.Default with
    {
        TimeZone = "Europe/Stockholm",
    };

    private static DateTimeOffset Utc(int month, int day, int hour, int minute = 0) =>
        new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void Nightly_backups_are_due_at_three_in_the_admins_time_zone()
    {
        // 03:00 in Stockholm in November is 02:00 UTC.
        Assert.Equal(Utc(11, 10, 2), Nightly.LatestDue(Utc(11, 10, 12)));
        Assert.Equal(Utc(11, 9, 2), Nightly.LatestDue(Utc(11, 10, 1, 59)));
        Assert.Equal(Utc(11, 11, 2), Nightly.NextDue(Utc(11, 10, 12)));
        Assert.Equal(Utc(11, 10, 2), Nightly.NextDue(Utc(11, 10, 1)));
    }

    [Fact]
    public void Weekly_backups_are_due_on_their_day()
    {
        var weekly = Nightly with
        {
            Frequency = BackupFrequency.Weekly,
            Weekday = DayOfWeek.Sunday,
        };

        // 10 November 2026 is a Tuesday; the Sunday before is the 8th.
        Assert.Equal(Utc(11, 8, 2), weekly.LatestDue(Utc(11, 10, 12)));
        Assert.Equal(Utc(11, 15, 2), weekly.NextDue(Utc(11, 10, 12)));
    }

    [Fact]
    public void Summer_time_moves_the_backup_with_the_clock()
    {
        // 03:00 in Stockholm in July is 01:00 UTC.
        Assert.Equal(Utc(7, 1, 1), Nightly.LatestDue(Utc(7, 1, 12)));
    }

    [Fact]
    public void Nothing_is_due_when_backups_are_off()
    {
        var off = Nightly with { Frequency = BackupFrequency.Off };
        Assert.Null(off.LatestDue(Utc(11, 10, 12)));
        Assert.Null(off.NextDue(Utc(11, 10, 12)));
    }

    [Theory]
    [InlineData(0, "Europe/Stockholm", false)]
    [InlineData(366, "Europe/Stockholm", false)]
    [InlineData(14, "Nowhere/City", false)]
    [InlineData(14, null, true)]
    [InlineData(365, "UTC", true)]
    public void Settings_are_checked(int keep, string? zone, bool valid) =>
        Assert.Equal(valid, (Nightly with { Keep = keep, TimeZone = zone }).IsValid);

    [Fact]
    public void Settings_round_trip_through_their_json()
    {
        var settings = new BackupSettings(
            BackupFrequency.Weekly,
            new TimeOnly(4, 30),
            DayOfWeek.Monday,
            "Europe/Stockholm",
            7
        );
        var json = JsonSerializer.SerializeToElement(SettingKeys.Backups.Write(settings));
        Assert.Equal(settings, SettingKeys.Backups.Read(json));
    }

    [Fact]
    public void Backup_files_are_named_by_their_start_in_utc()
    {
        var backup = Backup.Queue(BackupTrigger.Admin, Utc(11, 10, 3));
        backup.Start(
            new DateTimeOffset(2026, 11, 10, 4, 5, 6, TimeSpan.FromHours(1)),
            TimeSpan.FromHours(1)
        );
        Assert.Equal("dokument-20261110-030506.dump", backup.DatabaseFile);
        Assert.Equal("manifest-20261110-030506.jsonl.gz", backup.ManifestFile);

        // A second backup started in the same second takes the next free name.
        backup.TakeNextName();
        Assert.Equal("dokument-20261110-030507.dump", backup.DatabaseFile);
    }
}
