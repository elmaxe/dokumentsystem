using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Dokument.Domain.Audit;
using Dokument.Domain.Files;
using Dokument.Infrastructure.Persistence;
using Dokument.Server.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Dokument.IntegrationTests.FileTests;

namespace Dokument.IntegrationTests;

public sealed class StarredAndRecentTests(PostgresFixture postgres)
{
    private static readonly FakeUser Alice = new(
        "alice-sub",
        "Alice Andersson",
        "alice@example.com"
    );
    private static readonly FakeUser Bob = new("bob-sub", "Bob Berg", "bob@example.com");

    [Fact]
    public async Task Stars_are_each_persons_own_and_come_back_with_a_restore()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var alice = await SignInAsync(app, Alice);
        using var bob = await SignInAsync(app, Bob);
        var home = await CreateFolderAsync(alice, null, "Home");
        var contracts = await CreateFolderAsync(alice, home.Id, "Contracts");
        var lease = await UploadAsync(alice, "lease.pdf", Bytes(100), contracts.Id);
        var budget = await UploadAsync(alice, "budget.xlsx", Bytes(100), null);

        await PostIdsAsync(alice, "/api/v1/nodes/star", home.Id, lease);
        await PostIdsAsync(alice, "/api/v1/nodes/star", lease);
        var starred = await ListStarredAsync(alice);
        Assert.Equal(
            new HashSet<Guid> { home.Id, lease },
            starred.Items.Select(i => i.Node.Id).ToHashSet()
        );
        Assert.All(starred.Items, i => Assert.True(i.Node.Starred));
        Assert.Equal(
            ["Home", "Contracts"],
            starred.Items.Single(i => i.Node.Id == lease).Path.Select(p => p.Name)
        );
        Assert.Empty(starred.Items.Single(i => i.Node.Id == home.Id).Path);

        // Listings say what the person asking starred.
        var top = await ListAsync(alice, null);
        Assert.True(top.Items.Single(i => i.Id == home.Id).Starred);
        Assert.False(top.Items.Single(i => i.Id == budget).Starred);

        // Nobody else can star Alice's files, or see her stars.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (
                await bob.PostAsJsonAsync("/api/v1/nodes/star", new NodeIdsRequest([budget]))
            ).StatusCode
        );
        Assert.Empty((await ListStarredAsync(bob)).Items);

        // A trashed folder takes its starred contents out of the list; restoring brings both back.
        await PostIdsAsync(alice, "/api/v1/nodes/trash", home.Id);
        Assert.Empty((await ListStarredAsync(alice)).Items);
        await PostIdsAsync(alice, "/api/v1/trash/restore", home.Id);
        Assert.Equal(2, (await ListStarredAsync(alice)).Items.Count);

        await PostIdsAsync(alice, "/api/v1/nodes/unstar", home.Id, lease);
        Assert.Empty((await ListStarredAsync(alice)).Items);
        await PostIdsAsync(alice, "/api/v1/nodes/unstar", home.Id);
    }

    [Fact]
    public async Task Recent_lists_what_the_person_did_with_files_newest_first()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var alice = await SignInAsync(app, Alice);
        using var bob = await SignInAsync(app, Bob);
        var travel = await CreateFolderAsync(alice, null, "Travel");
        var list = await UploadAsync(alice, "packing.md", Bytes(10), travel.Id);
        var photo = await UploadAsync(alice, "photo.jpg", Bytes(20), null);
        var bill = await UploadAsync(alice, "bill.pdf", Bytes(30), null);

        var recent = await ListRecentAsync(alice);
        Assert.Equal([bill, photo, list], recent.Items.Select(i => i.Node.Id));
        Assert.All(recent.Items, i => Assert.Equal(RecentAction.Uploaded, i.Action));
        Assert.Equal(["Travel"], recent.Items[^1].Path.Select(p => p.Name));

        // Opening and downloading move a file up; renames and moves don't.
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await alice.PostAsync($"/api/v1/nodes/{list}/opened", null)).StatusCode
        );
        (await alice.GetAsync($"/api/v1/nodes/{photo}/content")).EnsureSuccessStatusCode();
        (
            await alice.PostAsJsonAsync($"/api/v1/nodes/{bill}/rename", new RenameRequest("el.pdf"))
        ).EnsureSuccessStatusCode();
        recent = await ListRecentAsync(alice);
        Assert.Equal([photo, list, bill], recent.Items.Select(i => i.Node.Id));
        Assert.Equal(
            [RecentAction.Downloaded, RecentAction.Opened, RecentAction.Uploaded],
            recent.Items.Select(i => i.Action)
        );

        // A new version counts as an upload; restoring an old one has its own label.
        await UploadAsync(alice, "el.pdf", Bytes(31), null, nodeId: bill);
        var versions = await VersionsAsync(alice, bill);
        (
            await alice.PostAsync($"/api/v1/nodes/{bill}/versions/{versions[^1].Id}/restore", null)
        ).EnsureSuccessStatusCode();
        recent = await ListRecentAsync(alice);
        Assert.Equal(bill, recent.Items[0].Node.Id);
        Assert.Equal(RecentAction.Restored, recent.Items[0].Action);

        // Folders can't be opened this way, and other people's files aren't anyone else's recent.
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await alice.PostAsync($"/api/v1/nodes/{travel.Id}/opened", null)).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await bob.PostAsync($"/api/v1/nodes/{list}/opened", null)).StatusCode
        );
        Assert.Empty((await ListRecentAsync(bob)).Items);

        // Trashed files drop out.
        await PostIdsAsync(alice, "/api/v1/nodes/trash", travel.Id);
        Assert.DoesNotContain(list, (await ListRecentAsync(alice)).Items.Select(i => i.Node.Id));

        // Opening is noted for Recent only: the audit log never records previews (§18).
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DokumentDbContext>();
        Assert.DoesNotContain(
            await db
                .AuditEvents.Where(e => e.TargetNodeId == list)
                .Select(e => e.Action)
                .ToListAsync(),
            a => a == AuditActions.FileDownload
        );
    }

    private static byte[] Bytes(int length) => RandomNumberGenerator.GetBytes(length);

    private static async Task PostIdsAsync(HttpClient client, string url, params Guid[] ids) =>
        (await client.PostAsJsonAsync(url, new NodeIdsRequest([.. ids]))).EnsureSuccessStatusCode();

    private static async Task<StarredListing> ListStarredAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<StarredListing>("/api/v1/starred", Json.Options))!;

    private static async Task<RecentListing> ListRecentAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<RecentListing>("/api/v1/recent", Json.Options))!;
}
