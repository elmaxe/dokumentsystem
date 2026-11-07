using System.Net;
using System.Net.Http.Json;
using Dokument.Domain.Audit;
using Dokument.Server.Files;
using static Dokument.IntegrationTests.FileTests;

namespace Dokument.IntegrationTests;

public sealed class LinkTests(PostgresFixture postgres)
{
    private static readonly FakeUser Alice = new(
        "alice-sub",
        "Alice Andersson",
        "alice@example.com"
    );
    private static readonly FakeUser Bob = new("bob-sub", "Bob Berg", "bob@example.com");

    [Fact]
    public async Task Anyone_with_a_folder_link_can_look_inside_and_only_there()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var alice = await SignInAsync(app, Alice);
        using var bob = await SignInAsync(app, Bob);
        using var stranger = app.CreateBrowser();
        var home = await CreateFolderAsync(alice, null, "Home");
        var trip = await CreateFolderAsync(alice, home.Id, "Gotland");
        var day = await CreateFolderAsync(alice, trip.Id, "Day 1");
        var photo = await UploadAsync(alice, "visby.txt", "Visby"u8.ToArray(), day.Id);
        var secret = await UploadAsync(alice, "secret.txt", "secret"u8.ToArray(), home.Id);

        // Alice turns on a link to the trip, view only; the folder now shows as shared.
        var link = await SaveLinkAsync(alice, trip.Id, new SaveLinkRequest(false, null, null));
        Assert.False(link.HasPassword);
        Assert.Null(link.ExpiresAt);
        Assert.Equal(0, link.Opens);
        Assert.True((await ListAsync(alice, home.Id)).Items.Single(i => i.Id == trip.Id).Shared);
        var token = TokenOf(link);

        // Someone who isn't signed in opens it, sees who shared it, and goes into Day 1.
        var page = await stranger.GetFromJsonAsync<LinkPage>(
            $"/api/v1/links/{token}",
            Json.Options
        );
        Assert.Equal("Gotland", page!.Node.Name);
        Assert.Equal("Alice Andersson", page.SharedBy);
        Assert.False(page.AllowDownload);
        var top = await LinkListAsync(stranger, token, null);
        Assert.Equal(["Gotland"], top.Path.Select(p => p.Name));
        Assert.Equal(day.Id, Assert.Single(top.Items).Id);
        var inside = await LinkListAsync(stranger, token, day.Id);
        Assert.Equal(["Gotland", "Day 1"], inside.Path.Select(p => p.Name));
        Assert.Equal(photo, Assert.Single(inside.Items).Id);
        var details = await stranger.GetFromJsonAsync<LinkNodeDetails>(
            $"/api/v1/links/{token}/nodes/{photo}",
            Json.Options
        );
        Assert.Equal(["Gotland", "Day 1"], details!.Path.Select(p => p.Name));

        // They can look at the file but not download it, and nothing outside the folder exists for them.
        var preview = await stranger.GetAsync($"/api/v1/links/{token}/nodes/{photo}/preview");
        Assert.Equal("Visby", await preview.Content.ReadAsStringAsync());
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await stranger.GetAsync($"/api/v1/links/{token}/nodes/{photo}/content")).StatusCode
        );
        foreach (var outside in new[] { secret, home.Id })
        {
            Assert.Equal(
                HttpStatusCode.NotFound,
                (await stranger.GetAsync($"/api/v1/links/{token}/nodes/{outside}")).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.NotFound,
                (
                    await stranger.GetAsync($"/api/v1/links/{token}/nodes?folderId={outside}")
                ).StatusCode
            );
        }

        // Only Alice sees and changes the link. She counts the open and allows downloads.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await bob.GetAsync($"/api/v1/nodes/{trip.Id}/link")).StatusCode
        );
        link = (
            await alice.GetFromJsonAsync<LinkDto>($"/api/v1/nodes/{trip.Id}/link", Json.Options)
        )!;
        Assert.Equal(1, link.Opens);
        Assert.NotNull(link.LastOpenedAt);
        link = await SaveLinkAsync(alice, trip.Id, new SaveLinkRequest(true, null, null));
        Assert.Equal(token, TokenOf(link));
        var download = await stranger.GetAsync($"/api/v1/links/{token}/nodes/{photo}/content");
        download.EnsureSuccessStatusCode();
        Assert.Equal("visby.txt", download.Content.Headers.ContentDisposition?.FileNameStar);

        // The download shows in the file's activity for Alice, as someone with the link.
        var activity = await alice.GetFromJsonAsync<ActivityListing>(
            $"/api/v1/nodes/{photo}/activity",
            Json.Options
        );
        var downloaded = activity!.Entries.First();
        Assert.Equal(AuditActions.FileDownload, downloaded.Action);
        Assert.True(downloaded.ViaLink);
        Assert.Null(downloaded.Actor);
        var tripActivity = await alice.GetFromJsonAsync<ActivityListing>(
            $"/api/v1/nodes/{trip.Id}/activity",
            Json.Options
        );
        Assert.Contains(tripActivity!.Entries, e => e.Action == AuditActions.LinkCreate);
        Assert.Contains(tripActivity.Entries, e => e.Action == AuditActions.LinkChange);
        Assert.DoesNotContain(tripActivity.Entries, e => e.Action == AuditActions.LinkAccess);

        // In the trash the link stops working; restored, it works again. Turned off, it's gone for good.
        await PostIdsAsync(alice, "/api/v1/nodes/trash", trip.Id);
        await AssertGoneAsync(stranger, token);
        await PostIdsAsync(alice, "/api/v1/trash/restore", trip.Id);
        (await stranger.GetAsync($"/api/v1/links/{token}")).EnsureSuccessStatusCode();
        (await alice.DeleteAsync($"/api/v1/nodes/{trip.Id}/link")).EnsureSuccessStatusCode();
        await AssertGoneAsync(stranger, token);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await alice.GetAsync($"/api/v1/nodes/{trip.Id}/link")).StatusCode
        );
        Assert.False((await ListAsync(alice, home.Id)).Items.Single(i => i.Id == trip.Id).Shared);

        // A new link is a new address.
        var again = await SaveLinkAsync(alice, trip.Id, new SaveLinkRequest(false, null, null));
        Assert.NotEqual(token, TokenOf(again));
        Assert.Equal(0, again.Opens);
        await AssertGoneAsync(stranger, "not-a-token");
    }

    [Fact]
    public async Task A_link_with_a_password_lets_in_only_whoever_typed_it()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var alice = await SignInAsync(app, Alice);
        using var stranger = app.CreateBrowser();
        using var other = app.CreateBrowser();
        var file = await UploadAsync(alice, "lease.txt", "lease"u8.ToArray(), null);

        // Too short a password and a day already past are refused.
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await alice.PutAsJsonAsync(
                    $"/api/v1/nodes/{file}/link",
                    new SaveLinkRequest(true, null, "abc")
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await alice.PutAsJsonAsync(
                    $"/api/v1/nodes/{file}/link",
                    new SaveLinkRequest(true, DateTimeOffset.UtcNow.AddDays(-1), null)
                )
            ).StatusCode
        );

        var expires = DateTimeOffset.UtcNow.AddDays(30);
        var link = await SaveLinkAsync(alice, file, new SaveLinkRequest(true, expires, "semester"));
        Assert.True(link.HasPassword);
        Assert.NotNull(link.ExpiresAt);
        var token = TokenOf(link);

        // Without the password the link shows nothing, not even the name.
        var locked = await stranger.GetAsync($"/api/v1/links/{token}");
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Equal("passwordRequired", await CodeAsync(locked));
        Assert.DoesNotContain("lease", await locked.Content.ReadAsStringAsync());
        var wrong = await stranger.PostAsJsonAsync(
            $"/api/v1/links/{token}/unlock",
            new UnlockLinkRequest("semestr")
        );
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("wrongPassword", await CodeAsync(wrong));

        // With it, this browser is let in, and only this one.
        (
            await stranger.PostAsJsonAsync(
                $"/api/v1/links/{token}/unlock",
                new UnlockLinkRequest("semester")
            )
        ).EnsureSuccessStatusCode();
        var page = await stranger.GetFromJsonAsync<LinkPage>(
            $"/api/v1/links/{token}",
            Json.Options
        );
        Assert.Equal("lease.txt", page!.Node.Name);
        Assert.NotNull(page.ExpiresAt);
        (
            await stranger.GetAsync($"/api/v1/links/{token}/nodes/{file}/content")
        ).EnsureSuccessStatusCode();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await other.GetAsync($"/api/v1/links/{token}")).StatusCode
        );

        // A new password ends that; keeping the password while changing something else doesn't.
        await SaveLinkAsync(alice, file, new SaveLinkRequest(false, expires, null));
        (await stranger.GetAsync($"/api/v1/links/{token}")).EnsureSuccessStatusCode();
        await SaveLinkAsync(alice, file, new SaveLinkRequest(false, expires, "vintern"));
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await stranger.GetAsync($"/api/v1/links/{token}")).StatusCode
        );

        // Taking the password off opens it to anyone with the link.
        link = await SaveLinkAsync(
            alice,
            file,
            new SaveLinkRequest(false, null, null, RemovePassword: true)
        );
        Assert.False(link.HasPassword);
        Assert.Null(link.ExpiresAt);
        (await other.GetAsync($"/api/v1/links/{token}")).EnsureSuccessStatusCode();
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await other.GetAsync($"/api/v1/links/{token}/nodes")).StatusCode
        );
    }

    private static async Task<LinkDto> SaveLinkAsync(
        HttpClient client,
        Guid nodeId,
        SaveLinkRequest request
    )
    {
        var response = await client.PutAsJsonAsync($"/api/v1/nodes/{nodeId}/link", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LinkDto>(Json.Options))!;
    }

    private static async Task<LinkFolder> LinkListAsync(
        HttpClient client,
        string token,
        Guid? folderId
    ) =>
        (
            await client.GetFromJsonAsync<LinkFolder>(
                $"/api/v1/links/{token}/nodes{(folderId is { } id ? $"?folderId={id}" : "")}",
                Json.Options
            )
        )!;

    private static string TokenOf(LinkDto link)
    {
        Assert.NotNull(link.Url);
        var at = link.Url.IndexOf("/s/", StringComparison.Ordinal);
        Assert.True(at > 0);
        return link.Url[(at + 3)..];
    }

    private static async Task AssertGoneAsync(HttpClient client, string token)
    {
        var response = await client.GetAsync($"/api/v1/links/{token}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("linkGone", await CodeAsync(response));
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>())?[
            "code"
        ]?.GetValue<string>();

    private static async Task PostIdsAsync(HttpClient client, string url, params Guid[] ids) =>
        (await client.PostAsJsonAsync(url, new NodeIdsRequest([.. ids]))).EnsureSuccessStatusCode();
}
