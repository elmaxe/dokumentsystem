using System.Net;
using System.Net.Http.Json;
using Dokument.Domain.Audit;
using Dokument.Domain.Sharing;
using Dokument.Infrastructure.Persistence;
using Dokument.Server.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Dokument.IntegrationTests.FileTests;

namespace Dokument.IntegrationTests;

public sealed class SharingTests(PostgresFixture postgres)
{
    private static readonly FakeUser Alice = new(
        "alice-sub",
        "Alice Andersson",
        "alice@example.com"
    );
    private static readonly FakeUser Bob = new("bob-sub", "Bob Berg", "bob@example.com");
    private static readonly FakeUser Carol = new(
        "carol-sub",
        "Carol Carlsson",
        "carol@example.com"
    );

    [Fact]
    public async Task A_viewer_sees_the_folder_and_can_change_nothing()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var alice = await SignInAsync(app, Alice);
        using var bob = await SignInAsync(app, Bob);
        using var carol = await SignInAsync(app, Carol);
        var bobId = await UserIdAsync(bob);
        var home = await CreateFolderAsync(alice, null, "Home");
        var family = await CreateFolderAsync(alice, home.Id, "Family");
        var lease = await UploadAsync(alice, "lease.pdf", Bytes(100), family.Id);

        // Alice finds Bob among the people, and shares the folder with him.
        var people = await alice.GetFromJsonAsync<List<PersonDto>>(
            "/api/v1/people?q=ber",
            Json.Options
        );
        Assert.Equal(bobId, Assert.Single(people!).Id);
        var access = await ShareAsync(alice, family.Id, bobId, ShareRole.Viewer);
        var entry = Assert.Single(access.People);
        Assert.Equal(ShareRole.Viewer, entry.Role);
        Assert.NotNull(entry.ShareId);
        Assert.Equal(NodeRole.Owner, access.YourAccess);
        Assert.True((await ListAsync(alice, home.Id)).Items.Single().Shared);

        // Bob sees it in Shared with me, New until he opens it, and never in his own My files.
        var shared = await SharedWithMeAsync(bob);
        var item = Assert.Single(shared);
        Assert.Equal(family.Id, item.Node.Id);
        Assert.True(item.New);
        Assert.Equal("Alice Andersson", item.Node.OwnerName);
        Assert.Equal(NodeRole.Viewer, item.Node.Access);
        Assert.Empty((await ListAsync(bob, null)).Items);
        (await bob.PostAsync($"/api/v1/nodes/{family.Id}/seen", null)).EnsureSuccessStatusCode();
        Assert.False(Assert.Single(await SharedWithMeAsync(bob)).New);

        // He can open the folder and the file; the path starts at the shared folder, not Alice's Home.
        var listing = await ListAsync(bob, family.Id);
        Assert.Equal(lease, Assert.Single(listing.Items).Id);
        Assert.Equal(["Family"], listing.Path.Select(p => p.Name));
        var details = await bob.GetFromJsonAsync<NodeDetails>(
            $"/api/v1/nodes/{lease}",
            Json.Options
        );
        Assert.Equal(["Family"], details!.Path.Select(p => p.Name));
        (await bob.GetAsync($"/api/v1/nodes/{lease}/content")).EnsureSuccessStatusCode();
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await bob.GetAsync($"/api/v1/nodes?folderId={home.Id}")).StatusCode
        );

        // But not change anything.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (
                await bob.PostAsJsonAsync(
                    $"/api/v1/nodes/{lease}/rename",
                    new RenameRequest("x.pdf")
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await CreateUploadAsync(bob, "x.txt", 1, family.Id, null)).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (
                await bob.PostAsJsonAsync("/api/v1/nodes/trash", new NodeIdsRequest([lease]))
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (
                await bob.PostAsJsonAsync(
                    "/api/v1/nodes/tags",
                    new ChangeTagsRequest([lease], ["receipt"], null)
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (
                await bob.PostAsJsonAsync(
                    $"/api/v1/nodes/{family.Id}/shares",
                    new ShareRequest(await UserIdAsync(carol), ShareRole.Viewer)
                )
            ).StatusCode
        );

        // He can star it, and sees who else has access.
        await PostIdsAsync(bob, "/api/v1/nodes/star", lease);
        var seen = await bob.GetFromJsonAsync<AccessDto>(
            $"/api/v1/nodes/{lease}/access",
            Json.Options
        );
        Assert.Equal("Alice Andersson", seen!.Owner.Name);
        Assert.Equal(NodeRole.Viewer, seen.YourAccess);
        Assert.Equal("Family", Assert.Single(seen.People).InheritedFrom!.Name);

        // Someone it isn't shared with sees nothing at all.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await carol.GetAsync($"/api/v1/nodes/{lease}")).StatusCode
        );
        Assert.Empty(await SharedWithMeAsync(carol));

        // Removing the share takes it away again, with Bob's star.
        (await alice.DeleteAsync($"/api/v1/shares/{entry.ShareId}")).EnsureSuccessStatusCode();
        Assert.Empty(await SharedWithMeAsync(bob));
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await bob.GetAsync($"/api/v1/nodes/{lease}")).StatusCode
        );
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DokumentDbContext>();
        Assert.False(
            await db.Stars.AnyAsync(s => s.UserId == bobId, TestContext.Current.CancellationToken)
        );
        Assert.Equal(
            [AuditActions.ShareGrant, AuditActions.ShareRevoke],
            await db
                .AuditEvents.Where(e => e.Action.StartsWith("share."))
                .OrderBy(e => e.At)
                .Select(e => e.Action)
                .ToListAsync(TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task An_editor_adds_files_to_the_owners_storage_and_trash()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var alice = await SignInAsync(app, Alice);
        using var bob = await SignInAsync(app, Bob);
        var bobId = await UserIdAsync(bob);
        var family = await CreateFolderAsync(alice, null, "Family");
        await ShareAsync(alice, family.Id, bobId, ShareRole.Editor);

        // What Bob adds belongs to Alice and uses her storage.
        var photos = await CreateFolderAsync(bob, family.Id, "Photos");
        var photo = await UploadAsync(bob, "beach.jpg", Bytes(500), photos.Id);
        var listed = Assert.Single((await ListAsync(alice, photos.Id)).Items);
        Assert.Equal(photo, listed.Id);
        Assert.Equal(await UserIdAsync(alice), listed.OwnerId);
        Assert.Equal(
            NodeRole.Editor,
            Assert.Single((await ListAsync(bob, photos.Id)).Items).Access
        );
        Assert.Equal(500, (await StorageAsync(alice)).UsedBytes);
        Assert.Equal(0, (await StorageAsync(bob)).UsedBytes);

        // He renames and tags, but can't keep versions forever or move things out to his own files.
        (
            await bob.PostAsJsonAsync(
                $"/api/v1/nodes/{photo}/rename",
                new RenameRequest("strand.jpg")
            )
        ).EnsureSuccessStatusCode();
        (
            await bob.PostAsJsonAsync(
                "/api/v1/nodes/tags",
                new ChangeTagsRequest([photo], ["summer"], null)
            )
        ).EnsureSuccessStatusCode();
        var version = Assert.Single(await VersionsAsync(bob, photo));
        Assert.Equal(bobId, version.UploadedBy);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (
                await bob.PutAsJsonAsync(
                    $"/api/v1/nodes/{photo}/versions/{version.Id}/keep-forever",
                    new KeepForeverRequest(true)
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await bob.PostAsJsonAsync($"/api/v1/nodes/{photo}/move", new MoveRequest(null))
            ).StatusCode
        );
        (
            await bob.PostAsJsonAsync($"/api/v1/nodes/{photo}/move", new MoveRequest(family.Id))
        ).EnsureSuccessStatusCode();

        // Trashing goes to Alice's trash, saying who did it; Bob can undo it but has no trash of his own for it.
        await PostIdsAsync(bob, "/api/v1/nodes/trash", photos.Id);
        Assert.Empty((await TrashAsync(bob)).Items);
        var trashed = Assert.Single((await TrashAsync(alice)).Items);
        Assert.Equal(photos.Id, trashed.Node.Id);
        Assert.Equal("Bob Berg", trashed.TrashedBy);
        await PostIdsAsync(bob, "/api/v1/trash/restore", photos.Id);
        Assert.Empty((await TrashAsync(alice)).Items);

        // Only the owner deletes forever.
        await PostIdsAsync(bob, "/api/v1/nodes/trash", photos.Id);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (
                await bob.PostAsJsonAsync("/api/v1/trash/delete", new NodeIdsRequest([photos.Id]))
            ).StatusCode
        );
    }

    [Fact]
    public async Task Shares_lower_down_only_add_and_Shared_with_me_lists_the_top()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var alice = await SignInAsync(app, Alice);
        using var bob = await SignInAsync(app, Bob);
        using var carol = await SignInAsync(app, Carol);
        var bobId = await UserIdAsync(bob);
        var carolId = await UserIdAsync(carol);
        var family = await CreateFolderAsync(alice, null, "Family");
        var trip = await CreateFolderAsync(alice, family.Id, "Trip");
        await ShareAsync(alice, family.Id, bobId, ShareRole.Viewer);

        // The same role or less below a folder that already gives it is refused; more is fine.
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await alice.PostAsJsonAsync(
                    $"/api/v1/nodes/{trip.Id}/shares",
                    new ShareRequest(bobId, ShareRole.Viewer)
                )
            ).StatusCode
        );
        var access = await ShareAsync(alice, trip.Id, bobId, ShareRole.Editor);
        var bobHere = Assert.Single(access.People);
        Assert.Equal(ShareRole.Editor, bobHere.Role);
        Assert.Equal(ShareRole.Viewer, bobHere.InheritedRole);

        // Bob sees only Family at the top of Shared with me, but can edit inside Trip.
        Assert.Equal(family.Id, Assert.Single(await SharedWithMeAsync(bob)).Node.Id);
        await CreateFolderAsync(bob, trip.Id, "Day 1");
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (
                await bob.PostAsJsonAsync(
                    "/api/v1/folders",
                    new CreateFolderRequest(family.Id, "x")
                )
            ).StatusCode
        );

        // Moving Trip to the top of Alice's files would leave Bob with only his own share on it; Carol, who sees
        // Family, would lose it.
        await ShareAsync(alice, family.Id, carolId, ShareRole.Viewer);
        var check = await (
            await alice.PostAsJsonAsync(
                "/api/v1/nodes/move-check",
                new MoveCheckRequest([trip.Id], null)
            )
        ).Content.ReadFromJsonAsync<MoveCheckResponse>(Json.Options);
        Assert.Equal(["Carol Carlsson"], check!.LosingAccess.Select(p => p.Name));

        // Bob leaves Family and keeps Trip, which now shows at the top.
        (await bob.PostAsync($"/api/v1/nodes/{family.Id}/leave", null)).EnsureSuccessStatusCode();
        Assert.Equal(trip.Id, Assert.Single(await SharedWithMeAsync(bob)).Node.Id);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await bob.GetAsync($"/api/v1/nodes?folderId={family.Id}")).StatusCode
        );
        Assert.Equal(["Trip"], (await ListAsync(bob, trip.Id)).Path.Select(p => p.Name));
    }

    [Fact]
    public async Task People_find_shared_files_in_search()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var alice = await SignInAsync(app, Alice);
        using var bob = await SignInAsync(app, Bob);
        var family = await CreateFolderAsync(alice, null, "Family");
        var lease = await UploadAsync(alice, "hyresavtal.pdf", Bytes(100), family.Id);
        await UploadAsync(bob, "hyresavtal-bob.pdf", Bytes(120), null);

        Assert.Single((await SearchAsync(bob, "hyresavtal")).Results);
        await ShareAsync(alice, family.Id, await UserIdAsync(bob), ShareRole.Viewer);
        Assert.Equal(2, (await SearchAsync(bob, "hyresavtal")).Results.Count);
        Assert.Equal(
            lease,
            Assert.Single((await SearchAsync(bob, "hyresavtal is:shared")).Results).Node.Id
        );
        Assert.NotEqual(
            lease,
            Assert.Single((await SearchAsync(bob, "hyresavtal owner:me")).Results).Node.Id
        );
        var hit = (await SearchAsync(bob, "hyresavtal is:shared")).Results.Single();
        Assert.Equal(["Family"], hit.Path.Select(p => p.Name));
    }

    private static async Task<AccessDto> ShareAsync(
        HttpClient client,
        Guid nodeId,
        Guid userId,
        ShareRole role
    )
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/nodes/{nodeId}/shares",
            new ShareRequest(userId, role)
        );
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccessDto>(Json.Options))!;
    }

    private static async Task<List<SharedItemDto>> SharedWithMeAsync(HttpClient client) =>
        (
            await client.GetFromJsonAsync<List<SharedItemDto>>(
                "/api/v1/shared-with-me",
                Json.Options
            )
        )!;

    private static async Task<Guid> UserIdAsync(HttpClient client) =>
        (await DokumentApp.GetSessionAsync(client)).User.Id;

    private static async Task<StorageUsage> StorageAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<StorageUsage>("/api/v1/storage", Json.Options))!;

    private static async Task<TrashListing> TrashAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<TrashListing>("/api/v1/trash", Json.Options))!;

    private static async Task<SearchResponse> SearchAsync(HttpClient client, string q) =>
        (
            await client.GetFromJsonAsync<SearchResponse>(
                $"/api/v1/search?q={Uri.EscapeDataString(q)}",
                Json.Options
            )
        )!;

    private static async Task PostIdsAsync(HttpClient client, string url, params Guid[] ids) =>
        (await client.PostAsJsonAsync(url, new NodeIdsRequest([.. ids]))).EnsureSuccessStatusCode();

    private static byte[] Bytes(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }
}
