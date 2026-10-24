using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Dokument.Domain.Audit;
using Dokument.Domain.Files;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Persistence;
using Dokument.Server.Admin;
using Dokument.Server.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Dokument.IntegrationTests.FileTests;

namespace Dokument.IntegrationTests;

public sealed class TrashTests(PostgresFixture postgres)
{
    private static readonly FakeUser Alice = new(
        "alice-sub",
        "Alice Andersson",
        "alice@example.com"
    );
    private static readonly FakeUser Bob = new("bob-sub", "Bob Berg", "bob@example.com");
    private static readonly FakeUser Admin = new(
        "admin-sub",
        "Anna Admin",
        "anna@example.com",
        "dokument-admins"
    );

    [Fact]
    public async Task A_folder_goes_to_the_trash_with_its_contents_and_comes_back()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var alice = await SignInAsync(app, Alice);
        var kvitton = await CreateFolderAsync(alice, null, "Kvitton");
        var year = await CreateFolderAsync(alice, kvitton.Id, "2025");
        var receipt = await UploadAsync(alice, "ikea.pdf", Bytes(1000), year.Id);
        var note = await UploadAsync(alice, "note.txt", Bytes(10), kvitton.Id);

        await TrashAsync(alice, note);
        await TrashAsync(alice, kvitton.Id);

        // Only what Alice trashed is listed; the folder's contents are counted in it.
        var trash = await ListTrashAsync(alice);
        Assert.Equal([kvitton.Id, note], trash.Items.Select(i => i.Node.Id));
        var folder = trash.Items[0];
        Assert.Equal(2, folder.ItemCount);
        Assert.Equal(1000, folder.Size);
        Assert.Equal(folder.TrashedAt.AddDays(30), folder.DeleteAt);
        Assert.Equal(["Kvitton"], trash.Items[1].Path.Select(p => p.Name));

        Assert.Empty((await ListAsync(alice, null)).Items);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await alice.GetAsync($"/api/v1/nodes/{receipt}/content")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await alice.GetAsync($"/api/v1/nodes?folderId={year.Id}")).StatusCode
        );

        // Its name is free again, so restoring it gives it a number.
        await CreateFolderAsync(alice, null, "Kvitton");
        var restored = await RestoreAsync(alice, kvitton.Id);
        Assert.Equal("Kvitton (1)", restored.Single().Node.Name);
        Assert.False(restored.Single().MovedToRoot);
        Assert.Equal(receipt, Assert.Single((await ListAsync(alice, year.Id)).Items).Id);

        // The note was trashed on its own, so it stayed until restored itself, back into its folder.
        Assert.DoesNotContain(note, (await ListAsync(alice, kvitton.Id)).Items.Select(i => i.Id));
        await RestoreAsync(alice, note);
        Assert.Contains(note, (await ListAsync(alice, kvitton.Id)).Items.Select(i => i.Id));
        Assert.Empty((await ListTrashAsync(alice)).Items);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DokumentDbContext>();
        var actions = await db
            .AuditEvents.Where(e => e.TargetNodeId == kvitton.Id)
            .OrderBy(e => e.At)
            .Select(e => e.Action)
            .ToListAsync();
        Assert.Equal(
            [AuditActions.FolderCreate, AuditActions.NodeTrash, AuditActions.NodeRestore],
            actions
        );
    }

    [Fact]
    public async Task An_item_whose_folder_is_in_the_trash_is_restored_to_my_files()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var alice = await SignInAsync(app, Alice);
        using var bob = await SignInAsync(app, Bob);
        var folder = await CreateFolderAsync(alice, null, "Arkiv");
        var file = await UploadAsync(alice, "gammal.pdf", Bytes(100), folder.Id);
        await TrashAsync(alice, file);
        await TrashAsync(alice, folder.Id);

        // Nobody else can see or touch Alice's trash.
        Assert.Empty((await ListTrashAsync(bob)).Items);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (
                await bob.PostAsJsonAsync("/api/v1/trash/restore", new NodeIdsRequest([file]))
            ).StatusCode
        );

        var restored = Assert.Single(await RestoreAsync(alice, file));
        Assert.True(restored.MovedToRoot);
        Assert.Null(restored.Node.ParentId);
        Assert.Equal(file, Assert.Single((await ListAsync(alice, null)).Items).Id);

        // A live item can't be restored, and a trashed one can't be trashed again.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (
                await alice.PostAsJsonAsync("/api/v1/trash/restore", new NodeIdsRequest([file]))
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (
                await alice.PostAsJsonAsync("/api/v1/nodes/trash", new NodeIdsRequest([folder.Id]))
            ).StatusCode
        );
    }

    [Fact]
    public async Task Delete_forever_frees_storage_and_the_purge_removes_the_bytes()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var alice = await SignInAsync(app, Alice);
        var bytes = Bytes(5000);
        var original = await UploadAsync(alice, "scan.pdf", bytes, null);
        var copy = await UploadAsync(alice, "scan-kopia.pdf", bytes, null);
        var other = await UploadAsync(alice, "other.bin", Bytes(300), null);
        Assert.Equal(5300, (await StorageAsync(alice)).UsedBytes);

        // The copy still holds the bytes, so trashing one frees nothing yet.
        await TrashAsync(alice, original);
        Assert.Equal(new StorageUsage(5300, null, 0), await StorageAsync(alice));
        await TrashAsync(alice, copy, other);
        Assert.Equal(new StorageUsage(5300, null, 5300), await StorageAsync(alice));
        Assert.Equal(5300, (await ListTrashAsync(alice)).FreeableBytes);

        (
            await alice.PostAsJsonAsync(
                "/api/v1/trash/delete",
                new NodeIdsRequest([original, copy])
            )
        ).EnsureSuccessStatusCode();
        Assert.Equal(new StorageUsage(300, null, 300), await StorageAsync(alice));

        var empty = await alice.PostAsync("/api/v1/trash/empty", null);
        Assert.Equal(1, (await empty.Content.ReadFromJsonAsync<EmptyTrashResponse>())!.Items);
        Assert.Equal(new StorageUsage(0, null, 0), await StorageAsync(alice));
        Assert.Empty((await ListTrashAsync(alice)).Items);

        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PurgeService>().RunAsync();
        var db = scope.ServiceProvider.GetRequiredService<DokumentDbContext>();
        Assert.Empty(await db.Blobs.ToListAsync());
        Assert.Empty(await db.Nodes.ToListAsync());
        Assert.Empty(
            Directory.GetFiles(
                Path.Combine(app.OriginalsPath, "blobs"),
                "*",
                SearchOption.AllDirectories
            )
        );
        var actions = await db.AuditEvents.Select(e => e.Action).ToListAsync();
        Assert.Equal(3, actions.Count(a => a == AuditActions.NodeDeleteForever));
        Assert.Single(actions, AuditActions.TrashEmpty);

        // The same bytes can be uploaded again afterwards.
        var again = await UploadAsync(alice, "scan.pdf", bytes, null);
        Assert.Equal(bytes, await alice.GetByteArrayAsync($"/api/v1/nodes/{again}/content"));
    }

    [Fact]
    public async Task The_purge_deletes_expired_trash_and_versions_past_retention()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var alice = await SignInAsync(app, Alice);
        using var admin = await SignInAsync(app, Admin);
        var file = await UploadAsync(alice, "budget.xlsx", Bytes(100), null);
        for (var i = 0; i < 4; i++)
        {
            await UploadAsync(alice, "budget.xlsx", Bytes(100), null, nodeId: file);
        }

        var versions = await VersionsAsync(alice, file);
        Assert.Equal([5, 4, 3, 2, 1], versions.Select(v => v.Number));
        (
            await alice.PutAsJsonAsync(
                $"/api/v1/nodes/{file}/versions/{versions.Single(v => v.Number == 1).Id}/keep-forever",
                new KeepForeverRequest(true)
            )
        ).EnsureSuccessStatusCode();

        var old = await UploadAsync(alice, "old.txt", Bytes(50), null);
        var recent = await UploadAsync(alice, "recent.txt", Bytes(50), null);
        await TrashAsync(alice, old, recent);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DokumentDbContext>();
        var purge = scope.ServiceProvider.GetRequiredService<PurgeService>();

        // Version 2 was replaced 31 days ago, and old.txt was trashed 31 days ago.
        await db.Database.ExecuteSqlAsync(
            $"UPDATE file_versions SET uploaded_at = now() - interval '31 days' WHERE node_id = {file} AND version_no = 3"
        );
        await db.Database.ExecuteSqlAsync(
            $"UPDATE nodes SET trashed_at = now() - interval '31 days' WHERE id = {old}"
        );
        await purge.RunAsync();
        Assert.Equal([5, 4, 3, 1], (await VersionsAsync(alice, file)).Select(v => v.Number));
        Assert.Equal([recent], (await ListTrashAsync(alice)).Items.Select(i => i.Node.Id));

        // At most 2 versions: 3 goes, the current one and the kept one stay whatever the count.
        (
            await admin.PutAsJsonAsync(
                "/api/v1/admin/settings/versions",
                new VersionSettingsDto(30, 2)
            )
        ).EnsureSuccessStatusCode();
        await purge.RunAsync();
        Assert.Equal([5, 4, 1], (await VersionsAsync(alice, file)).Select(v => v.Number));
        (
            await admin.PutAsJsonAsync(
                "/api/v1/admin/settings/versions",
                new VersionSettingsDto(30, 1)
            )
        ).EnsureSuccessStatusCode();
        await purge.RunAsync();
        Assert.Equal([5, 1], (await VersionsAsync(alice, file)).Select(v => v.Number));

        // Storage counts what's left: versions 5 and 1, and recent.txt in the trash.
        Assert.Equal(250, (await StorageAsync(alice)).UsedBytes);
        var system = await db
            .AuditEvents.Where(e => e.ActorKind == AuditActorKind.System)
            .Select(e => e.Action)
            .ToListAsync();
        Assert.Equal(3, system.Count(a => a == AuditActions.VersionPurge));
        Assert.Single(system, AuditActions.NodePurge);
        Assert.Equal(
            3,
            await db.Blobs.CountAsync() // versions 5 and 1, and recent.txt
        );
    }

    [Fact]
    public async Task Admins_set_how_long_the_trash_keeps_items()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var admin = await SignInAsync(app, Admin);
        using var alice = await SignInAsync(app, Alice);
        var settings = await admin.GetFromJsonAsync<AdminSettingsDto>(
            "/api/v1/admin/settings",
            Json.Options
        );
        Assert.Equal(new TrashSettingsDto(30), settings!.Trash);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await admin.PutAsJsonAsync("/api/v1/admin/settings/trash", new TrashSettingsDto(0))
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (
                await alice.PutAsJsonAsync("/api/v1/admin/settings/trash", new TrashSettingsDto(7))
            ).StatusCode
        );
        (
            await admin.PutAsJsonAsync("/api/v1/admin/settings/trash", new TrashSettingsDto(7))
        ).EnsureSuccessStatusCode();

        var file = await UploadAsync(alice, "a.txt", Bytes(10), null);
        await TrashAsync(alice, file);
        var item = Assert.Single((await ListTrashAsync(alice)).Items);
        Assert.Equal(item.TrashedAt.AddDays(7), item.DeleteAt);
        Assert.Equal(7, (await ListTrashAsync(alice)).KeepDays);
    }

    private static byte[] Bytes(int length) => RandomNumberGenerator.GetBytes(length);

    private static async Task TrashAsync(HttpClient client, params Guid[] ids) =>
        Assert.Equal(
            HttpStatusCode.NoContent,
            (
                await client.PostAsJsonAsync("/api/v1/nodes/trash", new NodeIdsRequest([.. ids]))
            ).StatusCode
        );

    private static async Task<List<RestoredItem>> RestoreAsync(HttpClient client, params Guid[] ids)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/trash/restore",
            new NodeIdsRequest([.. ids])
        );
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RestoreResponse>(Json.Options))!.Items;
    }

    private static async Task<TrashListing> ListTrashAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<TrashListing>("/api/v1/trash", Json.Options))!;

    private static async Task<StorageUsage> StorageAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<StorageUsage>("/api/v1/storage", Json.Options))!;
}
