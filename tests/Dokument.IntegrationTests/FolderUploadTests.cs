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

public sealed class FolderUploadTests(PostgresFixture postgres)
{
    private static readonly FakeUser Alice = new(
        "alice-sub",
        "Alice Andersson",
        "alice@example.com"
    );
    private static readonly FakeUser Bob = new("bob-sub", "Bob Berg", "bob@example.com");

    [Fact]
    public async Task A_folder_tree_is_made_in_one_go_with_the_folders_above_each_path()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var alice = await SignInAsync(app, Alice);
        var target = await CreateFolderAsync(alice, null, "Arkiv");

        var tree = await CreateTreeAsync(
            alice,
            new FolderTreeRequest(target.Id, ["Kvitton/2025/mars", "Kvitton/2024", "Kvitton/tom"])
        );

        Assert.Equal(
            ["Kvitton/2025/mars", "Kvitton/2024", "Kvitton/tom"],
            tree.Select(f => f.Path)
        );
        Assert.All(tree, f => Assert.True(f.Created));
        var kvitton = (await ListAsync(alice, target.Id)).Items.Single();
        Assert.Equal("Kvitton", kvitton.Name);
        Assert.Equal(
            ["2024", "2025", "tom"],
            (await ListAsync(alice, kvitton.Id)).Items.Select(i => i.Name).Order()
        );
        var mars = await ListAsync(alice, tree[0].Id);
        Assert.Equal(["Arkiv", "Kvitton", "2025", "mars"], mars.Path.Select(p => p.Name));

        // Files go into the folders as usual.
        await UploadAsync(alice, "ikea.pdf", Bytes(10), tree[0].Id);
        Assert.Equal("ikea.pdf", (await ListAsync(alice, tree[0].Id)).Items.Single().Name);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DokumentDbContext>();
        Assert.Equal(
            6,
            await db.AuditEvents.CountAsync(e => e.Action == AuditActions.FolderCreate)
        );
    }

    [Fact]
    public async Task A_taken_top_folder_name_is_refused_merged_or_kept_beside()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var alice = await SignInAsync(app, Alice);
        var kvitton = await CreateFolderAsync(alice, null, "Kvitton");
        var year = await CreateFolderAsync(alice, kvitton.Id, "2025");
        await UploadAsync(alice, "ikea.pdf", Bytes(10), year.Id);
        await UploadAsync(alice, "readme.txt", Bytes(10), kvitton.Id);

        // Fail: the client asks first. Nothing is made.
        var refused = await alice.PostAsJsonAsync(
            "/api/v1/folders/tree",
            new FolderTreeRequest(null, ["kvitton/2025", "kvitton/2026"])
        );
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Single((await ListAsync(alice, kvitton.Id)).Items, i => i.Kind == NodeKind.Folder);

        // Merge: existing folders are reused, case-insensitively, and say what they already hold.
        var merged = await CreateTreeAsync(
            alice,
            new FolderTreeRequest(
                null,
                ["kvitton/2025", "kvitton/2026"],
                FolderConflictPolicy.Merge
            )
        );
        Assert.Equal(year.Id, merged[0].Id);
        Assert.False(merged[0].Created);
        Assert.Equal(["ikea.pdf"], merged[0].ExistingNames);
        Assert.True(merged[1].Created);
        Assert.Empty(merged[1].ExistingNames);
        Assert.Equal(
            ["2025", "2026", "readme.txt"],
            (await ListAsync(alice, kvitton.Id)).Items.Select(i => i.Name).Order()
        );

        // Keep both: a numbered folder beside it, with everything below made new.
        var beside = await CreateTreeAsync(
            alice,
            new FolderTreeRequest(null, ["Kvitton/2025"], FolderConflictPolicy.KeepBoth)
        );
        Assert.True(beside[0].Created);
        var top = await ListAsync(alice, null);
        Assert.Equal(["Kvitton", "Kvitton (1)"], top.Items.Select(i => i.Name).Order());
    }

    [Fact]
    public async Task A_file_with_a_folders_name_gets_a_numbered_folder_beside_it_even_when_merging()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var alice = await SignInAsync(app, Alice);
        await UploadAsync(alice, "Foton", Bytes(10), null);

        var tree = await CreateTreeAsync(
            alice,
            new FolderTreeRequest(null, ["Foton"], FolderConflictPolicy.Merge)
        );

        Assert.Equal("Foton (1)", tree[0].Name);
        Assert.True(tree[0].Created);
    }

    [Fact]
    public async Task Nothing_is_made_when_the_files_dont_fit_or_a_name_is_invalid()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var alice = await SignInAsync(app, Alice);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DokumentDbContext>();
            await db.Users.ExecuteUpdateAsync(s => s.SetProperty(u => u.QuotaBytes, 1000));
        }

        var tooBig = await alice.PostAsJsonAsync(
            "/api/v1/folders/tree",
            new FolderTreeRequest(null, ["Foton"], TotalBytes: 1001)
        );
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);
        Assert.Contains("quotaExceeded", await tooBig.Content.ReadAsStringAsync());

        var invalid = await alice.PostAsJsonAsync(
            "/api/v1/folders/tree",
            new FolderTreeRequest(null, ["Foton/../x"])
        );
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Empty((await ListAsync(alice, null)).Items);

        Assert.Equal(
            HttpStatusCode.OK,
            (
                await alice.PostAsJsonAsync(
                    "/api/v1/folders/tree",
                    new FolderTreeRequest(null, ["Foton"], TotalBytes: 1000)
                )
            ).StatusCode
        );
    }

    [Fact]
    public async Task Nobody_else_can_make_folders_in_your_folders()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var alice = await SignInAsync(app, Alice);
        using var bob = await SignInAsync(app, Bob);
        var folder = await CreateFolderAsync(alice, null, "Privat");

        var response = await bob.PostAsJsonAsync(
            "/api/v1/folders/tree",
            new FolderTreeRequest(folder.Id, ["Intrång"])
        );

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty((await ListAsync(alice, folder.Id)).Items);
    }

    private static async Task<List<FolderTreeEntry>> CreateTreeAsync(
        HttpClient client,
        FolderTreeRequest request
    )
    {
        var response = await client.PostAsJsonAsync("/api/v1/folders/tree", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (
            await response.Content.ReadFromJsonAsync<FolderTreeResponse>(Json.Options)
        )!.Folders;
    }

    private static byte[] Bytes(int length) => RandomNumberGenerator.GetBytes(length);
}
