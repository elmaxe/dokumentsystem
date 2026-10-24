using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using Dokument.Domain.Audit;
using Dokument.Domain.Identity;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Settings;
using Dokument.Server.Admin;
using Dokument.Server.Auth;
using Dokument.Server.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dokument.IntegrationTests;

public sealed class AdminTests(PostgresFixture postgres)
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

    [Fact]
    public async Task Only_admins_reach_the_admin_settings()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var alice = await FileTests.SignInAsync(app, Alice);
        using var admin = await FileTests.SignInAsync(app, Admin);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await alice.GetAsync("/api/v1/admin/settings")).StatusCode
        );
        var settings = await admin.GetFromJsonAsync<AdminSettingsDto>(
            "/api/v1/admin/settings",
            Json.Options
        );
        Assert.Null(settings!.Storage.DefaultQuotaBytes);
        Assert.Equal(new VersionSettingsDto(30, 100), settings.Versions);
        Assert.Empty(settings.Ml.Endpoints);
        Assert.Null(settings.Scanner.Address);
        Assert.Equal(FakeOidcProvider.Issuer, settings.SignIn.Issuer);
    }

    [Fact]
    public async Task The_default_quota_applies_until_a_person_has_their_own()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var alice = await FileTests.SignInAsync(app, Alice);
        using var admin = await FileTests.SignInAsync(app, Admin);

        (
            await admin.PutAsJsonAsync(
                "/api/v1/admin/settings/storage",
                new StorageSettingsDto(1000)
            )
        ).EnsureSuccessStatusCode();
        Assert.Equal(new StorageUsage(0, 1000), await StorageAsync(alice));
        Assert.Equal(
            HttpStatusCode.RequestEntityTooLarge,
            (await FileTests.CreateUploadAsync(alice, "big.bin", 1500, null, null)).StatusCode
        );

        var users = await admin.GetFromJsonAsync<List<AdminUser>>(
            "/api/v1/admin/users",
            Json.Options
        );
        var aliceId = users!.Single(u => u.Email == Alice.Email).Id;
        Assert.Equal(
            new QuotaDto(UserQuotaKind.Default, null),
            users!.Single(u => u.Id == aliceId).Quota
        );

        var unlimited = await admin.PutAsJsonAsync(
            $"/api/v1/admin/users/{aliceId}/quota",
            new QuotaDto(UserQuotaKind.Unlimited, null),
            Json.Options
        );
        Assert.Null(
            (
                await unlimited.Content.ReadFromJsonAsync<AdminUser>(Json.Options)
            )!.EffectiveQuotaBytes
        );
        Assert.Equal(
            HttpStatusCode.Created,
            (await FileTests.CreateUploadAsync(alice, "big.bin", 1500, null, null)).StatusCode
        );

        (
            await admin.PutAsJsonAsync(
                $"/api/v1/admin/users/{aliceId}/quota",
                new QuotaDto(UserQuotaKind.Limit, 5000),
                Json.Options
            )
        ).EnsureSuccessStatusCode();
        Assert.Equal(new StorageUsage(0, 5000), await StorageAsync(alice));

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await admin.PutAsJsonAsync(
                    $"/api/v1/admin/users/{aliceId}/quota",
                    new QuotaDto(UserQuotaKind.Limit, 0),
                    Json.Options
                )
            ).StatusCode
        );

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DokumentDbContext>();
        var actions = await db.AuditEvents.Select(e => e.Action).ToListAsync();
        Assert.Contains(AuditActions.SettingsChange, actions);
        Assert.Equal(2, actions.Count(a => a == AuditActions.QuotaChange));
    }

    [Fact]
    public async Task ML_api_keys_are_kept_secret_and_survive_reordering()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var admin = await FileTests.SignInAsync(app, Admin);

        var saved = await admin.PutAsJsonAsync(
            "/api/v1/admin/settings/ml",
            new UpdateMlSettingsRequest([
                new MlEndpointUpdate("http://gaming-pc.lan:3003/", "s3cret", false),
                new MlEndpointUpdate("http://ml:3003", null, false),
            ])
        );
        saved.EnsureSuccessStatusCode();
        Assert.DoesNotContain("s3cret", await saved.Content.ReadAsStringAsync());

        (
            await admin.PutAsJsonAsync(
                "/api/v1/admin/settings/ml",
                new UpdateMlSettingsRequest([
                    new MlEndpointUpdate("http://ml:3003", null, true),
                    new MlEndpointUpdate("http://gaming-pc.lan:3003", null, true),
                ])
            )
        ).EnsureSuccessStatusCode();
        var settings = await admin.GetFromJsonAsync<AdminSettingsDto>(
            "/api/v1/admin/settings",
            Json.Options
        );
        Assert.Equal(
            [
                new MlEndpointDto("http://ml:3003", false),
                new MlEndpointDto("http://gaming-pc.lan:3003", true),
            ],
            settings!.Ml.Endpoints
        );

        // The key is stored encrypted, and decrypts back.
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<SettingsStore>();
        var key = (await store.GetAsync(SettingKeys.Ml, default)).Endpoints[1].ProtectedApiKey!;
        Assert.NotEqual("s3cret", key);
        Assert.Equal(
            "s3cret",
            scope.ServiceProvider.GetRequiredService<SecretProtector>().Unprotect(key)
        );

        var invalid = await admin.PutAsJsonAsync(
            "/api/v1/admin/settings/ml",
            new UpdateMlSettingsRequest([new MlEndpointUpdate("ftp://ml", null, false)])
        );
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task The_scanner_test_talks_to_clamd()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var admin = await FileTests.SignInAsync(app, Admin);
        using var clamd = new TcpListener(IPAddress.Loopback, 0);
        clamd.Start();
        var port = ((IPEndPoint)clamd.LocalEndpoint).Port;
        var answer = Task.Run(async () =>
        {
            using var client = await clamd.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[64];
            var read = await stream.ReadAsync(buffer);
            Assert.Equal("zVERSION\0", Encoding.ASCII.GetString(buffer, 0, read));
            await stream.WriteAsync("ClamAV 1.4.3/27436/Thu Oct 23 08:00:00 2026\0"u8.ToArray());
        });

        var result = await admin.PostAsJsonAsync(
            "/api/v1/admin/scanner/test",
            new TestScannerRequest($"tcp://127.0.0.1:{port}")
        );
        var probe = await result.Content.ReadFromJsonAsync<ProbeResult>(Json.Options);
        await answer;
        Assert.True(probe!.Reachable);
        Assert.StartsWith("ClamAV 1.4.3", probe.Version);

        clamd.Stop();
        var down = await admin.PostAsJsonAsync(
            "/api/v1/admin/scanner/test",
            new TestScannerRequest($"127.0.0.1:{port}")
        );
        Assert.False((await down.Content.ReadFromJsonAsync<ProbeResult>(Json.Options))!.Reachable);

        (
            await admin.PutAsJsonAsync(
                "/api/v1/admin/settings/scanner",
                new ScannerSettingsDto("clamav")
            )
        ).EnsureSuccessStatusCode();
        var settings = await admin.GetFromJsonAsync<AdminSettingsDto>(
            "/api/v1/admin/settings",
            Json.Options
        );
        Assert.Equal("tcp://clamav:3310", settings!.Scanner.Address);
    }

    [Fact]
    public async Task People_pick_their_own_language()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var alice = await FileTests.SignInAsync(app, Alice);

        (
            await alice.PutAsJsonAsync("/api/v1/me/language", new SetLanguageRequest("sv"))
        ).EnsureSuccessStatusCode();
        Assert.Equal("sv", (await DokumentApp.GetSessionAsync(alice)).User.Language);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await alice.PutAsJsonAsync("/api/v1/me/language", new SetLanguageRequest("de"))
            ).StatusCode
        );

        (
            await alice.PutAsJsonAsync("/api/v1/me/language", new SetLanguageRequest(null))
        ).EnsureSuccessStatusCode();
        Assert.Null((await DokumentApp.GetSessionAsync(alice)).User.Language);
    }

    private static async Task<StorageUsage?> StorageAsync(HttpClient client) =>
        await client.GetFromJsonAsync<StorageUsage>("/api/v1/storage", Json.Options);
}
