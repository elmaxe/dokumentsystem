using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dokument.Domain.Backups;
using Dokument.Server.Admin;
using Dokument.Server.Files;
using Npgsql;

namespace Dokument.IntegrationTests;

/// <summary>
/// Database backups (docs/design.md §19), including the restore procedure: a dump restored into an empty database
/// starts the app with every file where it was. They need a <c>pg_dump</c> and <c>pg_restore</c> of our Postgres
/// image's major version (17): on PATH, or named by <c>DOKUMENT_TEST_PG_DUMP</c> and <c>DOKUMENT_TEST_PG_RESTORE</c>.
/// </summary>
public sealed class BackupTests(PostgresFixture postgres)
{
    private static readonly FakeUser Admin = new(
        "admin-sub",
        "Anna Admin",
        "anna@example.com",
        "dokument-admins"
    );
    private static readonly FakeUser Alice = new(
        "alice-sub",
        "Alice Andersson",
        "alice@example.com"
    );

    private static string PgDump =>
        Environment.GetEnvironmentVariable("DOKUMENT_TEST_PG_DUMP") ?? "pg_dump";

    private static string PgRestore =>
        Environment.GetEnvironmentVariable("DOKUMENT_TEST_PG_RESTORE") ?? "pg_restore";

    private static Task<DokumentApp> StartAsync(
        PostgresFixture postgres,
        string? pgDump = null,
        string? database = null,
        string? originals = null
    )
    {
        var settings = new Dictionary<string, string?>
        {
            ["Backups:PgDumpCommand"] = pgDump ?? PgDump,
        };
        if (originals is not null)
        {
            settings["Storage:OriginalsPath"] = originals;
        }

        return DokumentApp.StartAsync(postgres, settings, database: database);
    }

    [Fact]
    public async Task Back_up_now_writes_a_dump_and_a_name_manifest_that_restore_the_drive()
    {
        await using var app = await StartAsync(postgres);
        using var alice = await FileTests.SignInAsync(app, Alice);
        using var admin = await FileTests.SignInAsync(app, Admin);
        var receipts = await FileTests.CreateFolderAsync(alice, null, "Kvitton");
        var year = await FileTests.CreateFolderAsync(alice, receipts.Id, "2024");
        var bytes = Encoding.UTF8.GetBytes("kvitto nr 4471");
        var receipt = await FileTests.UploadAsync(alice, "ikea.txt", bytes, year.Id);
        await FileTests.UploadAsync(alice, "ikea.txt", [1, 2, 3], year.Id, nodeId: receipt);

        var started = await admin.PostAsync("/api/v1/admin/backups", null);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await admin.PostAsync("/api/v1/admin/backups", null)).StatusCode
        );
        var backup = await WaitForBackupAsync(admin);
        Assert.True(backup.Status == BackupStatus.Done, $"{backup.Error}: {backup.ErrorDetail}");
        Assert.Equal(BackupTrigger.Admin, backup.Trigger);
        Assert.Equal(Admin.Name, backup.RequestedBy);

        var dump = Path.Combine(app.BackupsPath, backup.DatabaseFile!);
        Assert.Equal(new FileInfo(dump).Length, backup.DatabaseBytes);
        var manifest = await ReadManifestAsync(
            Path.Combine(
                app.BackupsPath,
                backup.DatabaseFile!.Replace("dokument-", "manifest-").Replace(".dump", ".jsonl.gz")
            )
        );
        Assert.Equal(2, manifest.Count);
        var current = manifest.Single(l => l.GetProperty("current").GetBoolean());
        Assert.Equal(2, current.GetProperty("version").GetInt32());
        Assert.Equal("ikea.txt", current.GetProperty("name").GetString());
        Assert.Equal(
            ["Kvitton", "2024"],
            current.GetProperty("path").EnumerateArray().Select(p => p.GetString()).ToArray()
        );
        Assert.Equal(Alice.Email, current.GetProperty("owner").GetProperty("email").GetString());
        var first = manifest.Single(l => !l.GetProperty("current").GetBoolean());
        Assert.Equal(FileTests.Sha256(bytes), first.GetProperty("hash").GetString());
        Assert.True(
            File.Exists(Path.Combine(app.OriginalsPath, first.GetProperty("blob").GetString()!))
        );

        var listing = await admin.GetFromJsonAsync<BackupsDto>(
            "/api/v1/admin/backups",
            Json.Options
        );
        Assert.Equal(1, listing!.Count);
        Assert.Equal(backup.DatabaseBytes + backup.ManifestBytes, listing.TotalBytes);
        Assert.Equal(new TimeOnly(3, 0), TimeOnly.Parse(listing.Settings.At));

        // The restore procedure (§19): restore the dump into an empty database, start, and everything is there.
        var restored = await postgres.CreateDatabaseAsync();
        await RestoreAsync(dump, restored);
        await using var again = await StartAsync(
            postgres,
            database: restored,
            originals: app.OriginalsPath
        );
        using var aliceAgain = await FileTests.SignInAsync(again, Alice);
        var top = await FileTests.ListAsync(aliceAgain, null);
        var kvitton = top.Items.Single(i => i.Name == "Kvitton");
        var inside = await FileTests.ListAsync(
            aliceAgain,
            (await FileTests.ListAsync(aliceAgain, kvitton.Id)).Items.Single().Id
        );
        var file = inside.Items.Single();
        Assert.Equal("ikea.txt", file.Name);
        Assert.Equal(
            [1, 2, 3],
            await (
                await aliceAgain.GetAsync($"/api/v1/nodes/{file.Id}/content")
            ).Content.ReadAsByteArrayAsync()
        );
        Assert.Equal(2, (await FileTests.VersionsAsync(aliceAgain, file.Id)).Count);

        // The search indexes left out of the restore are made again on start, so names are found.
        var found = await aliceAgain.GetFromJsonAsync<SearchResponse>(
            "/api/v1/search?q=ikea",
            Json.Options
        );
        Assert.Contains(found!.Results, r => r.Node.Id == file.Id);
    }

    [Fact]
    public async Task A_failed_backup_shows_a_banner_until_dismissed()
    {
        await using var app = await StartAsync(postgres, pgDump: "/nowhere/pg_dump");
        using var admin = await FileTests.SignInAsync(app, Admin);
        var none = await admin.GetFromJsonAsync<AdminAlertsDto>(
            "/api/v1/admin/alerts",
            Json.Options
        );
        Assert.Null(none!.Backup);

        (await admin.PostAsync("/api/v1/admin/backups", null)).EnsureSuccessStatusCode();
        var backup = await WaitForBackupAsync(admin);
        Assert.Equal(BackupStatus.Failed, backup.Status);
        Assert.Equal(BackupError.DumpFailed, backup.Error);
        Assert.Empty(Directory.EnumerateFiles(app.BackupsPath));

        var alerts = await admin.GetFromJsonAsync<AdminAlertsDto>(
            "/api/v1/admin/alerts",
            Json.Options
        );
        Assert.Equal(backup.Id, alerts!.Backup!.Failed.Id);
        Assert.Null(alerts.Backup.LastGoodAt);

        (
            await admin.PostAsync($"/api/v1/admin/backups/{backup.Id}/dismiss", null)
        ).EnsureSuccessStatusCode();
        alerts = await admin.GetFromJsonAsync<AdminAlertsDto>("/api/v1/admin/alerts", Json.Options);
        Assert.Null(alerts!.Backup);
    }

    [Fact]
    public async Task Only_the_newest_backups_are_kept()
    {
        await using var app = await StartAsync(postgres);
        using var admin = await FileTests.SignInAsync(app, Admin);
        (
            await admin.PutAsJsonAsync(
                "/api/v1/admin/settings/backups",
                new BackupSettingsDto(
                    BackupFrequency.Weekly,
                    "04:30",
                    DayOfWeek.Monday,
                    "Europe/Stockholm",
                    1
                ),
                Json.Options
            )
        ).EnsureSuccessStatusCode();
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await admin.PutAsJsonAsync(
                    "/api/v1/admin/settings/backups",
                    new BackupSettingsDto(
                        BackupFrequency.Daily,
                        "03:00",
                        DayOfWeek.Monday,
                        "Nowhere/City",
                        3
                    ),
                    Json.Options
                )
            ).StatusCode
        );

        (await admin.PostAsync("/api/v1/admin/backups", null)).EnsureSuccessStatusCode();
        var first = await WaitForBackupAsync(admin);
        (await admin.PostAsync("/api/v1/admin/backups", null)).EnsureSuccessStatusCode();
        var second = await WaitForBackupAsync(admin, after: first.Id);
        Assert.Equal(BackupStatus.Done, second.Status);

        var listing = await admin.GetFromJsonAsync<BackupsDto>(
            "/api/v1/admin/backups",
            Json.Options
        );
        Assert.Equal(second.Id, Assert.Single(listing!.Backups).Id);
        Assert.Equal(BackupFrequency.Weekly, listing.Settings.Frequency);
        Assert.Equal("04:30", listing.Settings.At);
        Assert.Equal(
            DayOfWeek.Monday,
            listing.NextAt!.Value.ToOffset(TimeSpan.FromHours(1)).DayOfWeek
        );
        Assert.Equal(
            [
                second.DatabaseFile!,
                second
                    .DatabaseFile!.Replace("dokument-", "manifest-")
                    .Replace(".dump", ".jsonl.gz"),
            ],
            Directory.EnumerateFiles(app.BackupsPath).Select(Path.GetFileName).Order().ToArray()
        );
    }

    /// <summary>Waits until the newest backup (one other than <paramref name="after"/>) has finished.</summary>
    private static async Task<BackupDto> WaitForBackupAsync(HttpClient admin, Guid? after = null)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            var listing = await admin.GetFromJsonAsync<BackupsDto>(
                "/api/v1/admin/backups",
                Json.Options
            );
            if (
                listing!.Backups.FirstOrDefault() is { } newest
                && newest.Id != after
                && newest.Status is BackupStatus.Done or BackupStatus.Failed
            )
            {
                return newest;
            }

            Assert.True(DateTimeOffset.UtcNow < deadline, "The backup didn't finish in time.");
            await Task.Delay(250);
        }
    }

    private static async Task<List<JsonElement>> ReadManifestAsync(string path)
    {
        await using var file = File.OpenRead(path);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var lines = new List<JsonElement>();
        while (await reader.ReadLineAsync() is { } line)
        {
            lines.Add(JsonDocument.Parse(line).RootElement.Clone());
        }

        return lines;
    }

    /// <summary>
    /// The restore procedure from docs/design.md §19: everything but the BM25 indexes, which pg_search 0.20 can't read
    /// back from a dump; the server makes them again when it starts.
    /// </summary>
    private static async Task RestoreAsync(string dump, string connectionString)
    {
        var list = await RunAsync(PgRestore, connectionString, "--list", dump);
        var listFile = Path.GetTempFileName();
        await File.WriteAllLinesAsync(
            listFile,
            list.Split('\n').Where(line => !line.Contains("_bm25", StringComparison.Ordinal))
        );
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database;
        await RunAsync(
            PgRestore,
            connectionString,
            "--no-owner",
            "--exit-on-error",
            $"--use-list={listFile}",
            $"--dbname={database}",
            dump
        );
        File.Delete(listFile);
    }

    private static async Task<string> RunAsync(
        string command,
        string connectionString,
        params string[] arguments
    )
    {
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        var start = new ProcessStartInfo(command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["PGHOST"] = connection.Host;
        start.Environment["PGPORT"] = connection.Port.ToString(
            System.Globalization.CultureInfo.InvariantCulture
        );
        start.Environment["PGUSER"] = connection.Username;
        start.Environment["PGPASSWORD"] = connection.Password;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, errors);
        return await output;
    }
}
