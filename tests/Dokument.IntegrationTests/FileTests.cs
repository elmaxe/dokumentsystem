using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Dokument.Domain.Audit;
using Dokument.Domain.Files;
using Dokument.Infrastructure.Persistence;
using Dokument.Server.Auth;
using Dokument.Server.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dokument.IntegrationTests;

public sealed class FileTests(PostgresFixture postgres)
{
    private static readonly FakeUser Alice = new(
        "alice-sub",
        "Alice Andersson",
        "alice@example.com"
    );
    private static readonly FakeUser Bob = new("bob-sub", "Bob Berg", "bob@example.com");

    [Fact]
    public async Task Folders_can_be_created_renamed_and_moved()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var alice = await SignInAsync(app, Alice);

        var kvitton = await CreateFolderAsync(alice, null, "Kvitton");
        var year = await CreateFolderAsync(alice, kvitton.Id, "2025");
        var archive = await CreateFolderAsync(alice, null, "Arkiv");

        var clash = await alice.PostAsJsonAsync(
            "/api/v1/folders",
            new CreateFolderRequest(null, "kvitton")
        );
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Contains("nameTaken", await clash.Content.ReadAsStringAsync());

        var renamed = await alice.PostAsJsonAsync(
            $"/api/v1/nodes/{year.Id}/rename",
            new RenameRequest("År 2025")
        );
        Assert.Equal(
            "År 2025",
            (await renamed.Content.ReadFromJsonAsync<NodeDto>(Json.Options))!.Name
        );

        var intoItself = await alice.PostAsJsonAsync(
            $"/api/v1/nodes/{kvitton.Id}/move",
            new MoveRequest(year.Id)
        );
        Assert.Equal(HttpStatusCode.BadRequest, intoItself.StatusCode);

        (
            await alice.PostAsJsonAsync(
                $"/api/v1/nodes/{kvitton.Id}/move",
                new MoveRequest(archive.Id)
            )
        ).EnsureSuccessStatusCode();
        var listing = await ListAsync(alice, kvitton.Id);
        Assert.Equal(["Arkiv", "Kvitton"], listing.Path.Select(p => p.Name));
        Assert.Equal("År 2025", Assert.Single(listing.Items).Name);

        // The grandchild's path followed the move, so its breadcrumbs are right.
        var details = await alice.GetFromJsonAsync<NodeDetails>(
            $"/api/v1/nodes/{year.Id}",
            Json.Options
        );
        Assert.Equal(["Arkiv", "Kvitton"], details!.Path.Select(p => p.Name));
        Assert.Equal(["Arkiv"], (await ListAsync(alice, null)).Items.Select(i => i.Name));
    }

    [Fact]
    public async Task A_tus_upload_resumes_completes_and_downloads()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var alice = await SignInAsync(app, Alice);
        var folder = await CreateFolderAsync(alice, null, "Kvitton");
        var bytes = RandomNumberGenerator.GetBytes(300_000);

        var create = await CreateUploadAsync(
            alice,
            "ikea.pdf",
            bytes.Length,
            folder.Id,
            Sha256(bytes)
        );
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var location = create.Headers.Location!.ToString();

        var first = await PatchAsync(alice, location, 0, bytes[..100_000]);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.False(first.Headers.Contains(TusEndpoints.NodeIdHeader));

        var head = await alice.SendAsync(Tus(HttpMethod.Head, location));
        Assert.Equal("100000", head.Headers.GetValues("Upload-Offset").Single());

        var wrongOffset = await PatchAsync(alice, location, 0, bytes[..10]);
        Assert.Equal(HttpStatusCode.Conflict, wrongOffset.StatusCode);

        var last = await PatchAsync(alice, location, 100_000, bytes[100_000..]);
        Assert.Equal(HttpStatusCode.NoContent, last.StatusCode);
        var nodeId = Guid.Parse(last.Headers.GetValues(TusEndpoints.NodeIdHeader).Single());

        var file = Assert.Single((await ListAsync(alice, folder.Id)).Items);
        Assert.Equal(nodeId, file.Id);
        Assert.Equal("application/pdf", file.MimeType);
        Assert.Equal(bytes.Length, file.Size);

        var download = await alice.GetAsync($"/api/v1/nodes/{nodeId}/content");
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());

        // A ranged read later in the file (a resumed download) isn't audited again.
        var ranged = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/nodes/{nodeId}/content");
        ranged.Headers.Range = new RangeHeaderValue(1000, 1999);
        var part = await alice.SendAsync(ranged);
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal(bytes[1000..2000], await part.Content.ReadAsByteArrayAsync());

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DokumentDbContext>();
        var actions = await db
            .AuditEvents.Where(e => e.TargetNodeId == nodeId)
            .Select(e => e.Action)
            .ToListAsync();
        Assert.Equal(
            [AuditActions.FileDownload, AuditActions.FileUpload],
            actions.Order(StringComparer.Ordinal)
        );
        var user = await db.Users.SingleAsync();
        Assert.Equal(bytes.Length, user.UsedBytes);
        Assert.Equal(0, user.ReservedBytes);
        Assert.Empty(Directory.GetFiles(Path.Combine(app.OriginalsPath, "tmp")));
    }

    [Fact]
    public async Task Duplicates_are_reported_and_copied_without_sending_bytes()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var alice = await SignInAsync(app, Alice);
        using var bob = await SignInAsync(app, Bob);
        var bytes = Encoding.UTF8.GetBytes("kvitto nr 4471");
        var folder = await CreateFolderAsync(alice, null, "Kvitton");
        await UploadAsync(alice, "ikea.txt", bytes, folder.Id);

        var check = await alice.PostAsJsonAsync(
            "/api/v1/uploads/check",
            new DuplicateCheckRequest([
                new DuplicateCheckItem("copy.txt", bytes.Length, Sha256(bytes)),
                new DuplicateCheckItem("new.txt", 3, Sha256([1, 2, 3])),
            ])
        );
        var results = (
            await check.Content.ReadFromJsonAsync<DuplicateCheckResponse>(Json.Options)
        )!.Files;
        var match = Assert.Single(results[0].Duplicates);
        Assert.Equal("ikea.txt", match.Name);
        Assert.Equal(["Kvitton"], match.Path.Select(p => p.Name));
        Assert.Empty(results[1].Duplicates);

        // Bob never learns that Alice has the file, and can't copy it by hash.
        var bobCheck = await bob.PostAsJsonAsync(
            "/api/v1/uploads/check",
            new DuplicateCheckRequest([
                new DuplicateCheckItem("x.txt", bytes.Length, Sha256(bytes)),
            ])
        );
        Assert.Empty(
            (await bobCheck.Content.ReadFromJsonAsync<DuplicateCheckResponse>(Json.Options))!
                .Files[0]
                .Duplicates
        );
        var bobCopy = await bob.PostAsJsonAsync(
            "/api/v1/uploads/copy",
            new CopyFromContentRequest(null, "x.txt", Sha256(bytes), null)
        );
        Assert.Equal(HttpStatusCode.NotFound, bobCopy.StatusCode);

        var copy = await alice.PostAsJsonAsync(
            "/api/v1/uploads/copy",
            new CopyFromContentRequest(null, "copy.txt", Sha256(bytes), null)
        );
        Assert.Equal(HttpStatusCode.Created, copy.StatusCode);
        var copied = (await copy.Content.ReadFromJsonAsync<NodeDto>(Json.Options))!;
        Assert.Equal(bytes, await alice.GetByteArrayAsync($"/api/v1/nodes/{copied.Id}/content"));

        // Bob uploads the same bytes for real: stored once on disk, but counted against his own quota.
        await UploadAsync(bob, "mine.txt", bytes, null);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DokumentDbContext>();
        var blob = await db.Blobs.SingleAsync();
        Assert.Equal(3, blob.RefCount);
        Assert.All(await db.Users.ToListAsync(), u => Assert.Equal(bytes.Length, u.UsedBytes));
    }

    [Fact]
    public async Task A_hash_mismatch_discards_the_upload_and_releases_the_quota()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var alice = await SignInAsync(app, Alice);
        var bytes = Encoding.UTF8.GetBytes("changed while uploading");

        var create = await CreateUploadAsync(alice, "a.txt", bytes.Length, null, Sha256([1, 2, 3]));
        var patch = await PatchAsync(alice, create.Headers.Location!.ToString(), 0, bytes);

        Assert.Equal(HttpStatusCode.Conflict, patch.StatusCode);
        Assert.Contains("SHA-256", await patch.Content.ReadAsStringAsync());
        Assert.Equal(
            HttpStatusCode.NotFound,
            (
                await alice.SendAsync(Tus(HttpMethod.Head, create.Headers.Location!.ToString()))
            ).StatusCode
        );
        Assert.Empty((await ListAsync(alice, null)).Items);
        await using var scope = app.Services.CreateAsyncScope();
        var user = await scope
            .ServiceProvider.GetRequiredService<DokumentDbContext>()
            .Users.SingleAsync();
        Assert.Equal(0, user.ReservedBytes);
        Assert.Equal(0, user.UsedBytes);
    }

    [Fact]
    public async Task Name_clashes_fail_keep_both_or_become_a_new_version()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var alice = await SignInAsync(app, Alice);
        var original = await UploadAsync(alice, "Rapport.pdf", [1], null);

        var refused = await CreateUploadAsync(alice, "rapport.pdf", 1, null, null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains(original.ToString(), await refused.Content.ReadAsStringAsync());

        var kept = await UploadAsync(alice, "rapport.pdf", [2], null, onConflict: "keepBoth");
        var version = await UploadAsync(alice, "Rapport.pdf", [3], null, onConflict: "newVersion");
        Assert.Equal(original, version);

        var names = (await ListAsync(alice, null))
            .Items.Select(i => i.Name)
            .Order(StringComparer.Ordinal);
        Assert.Equal(["Rapport.pdf", "rapport (1).pdf"], names);
        Assert.NotEqual(original, kept);
    }

    [Fact]
    public async Task Versions_can_be_listed_restored_and_kept_forever()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var alice = await SignInAsync(app, Alice);
        var id = await UploadAsync(alice, "notes.txt", "v1"u8.ToArray(), null);
        await UploadAsync(alice, "notes.txt", "v2"u8.ToArray(), null, nodeId: id);

        // The same bytes again don't make a version (§6).
        await UploadAsync(alice, "notes.txt", "v2"u8.ToArray(), null, nodeId: id);
        var versions = await VersionsAsync(alice, id);
        Assert.Equal([2, 1], versions.Select(v => v.Number));
        Assert.True(versions[0].Current);
        Assert.Null(versions[0].PurgeAt);
        Assert.NotNull(versions[1].PurgeAt);

        var keep = await alice.PutAsJsonAsync(
            $"/api/v1/nodes/{id}/versions/{versions[1].Id}/keep-forever",
            new KeepForeverRequest(true)
        );
        var kept = (await keep.Content.ReadFromJsonAsync<VersionDto>(Json.Options))!;
        Assert.True(kept.KeepForever);
        Assert.Null(kept.PurgeAt);

        var restore = await alice.PostAsync(
            $"/api/v1/nodes/{id}/versions/{versions[1].Id}/restore",
            null
        );
        var restored = (await restore.Content.ReadFromJsonAsync<VersionDto>(Json.Options))!;
        Assert.Equal(3, restored.Number);
        Assert.Equal(1, restored.RestoredFrom);
        Assert.True(restored.Current);
        Assert.Equal(
            "v1"u8.ToArray(),
            await alice.GetByteArrayAsync($"/api/v1/nodes/{id}/content")
        );
        Assert.Equal(
            "v2"u8.ToArray(),
            await alice.GetByteArrayAsync($"/api/v1/nodes/{id}/content?versionId={versions[0].Id}")
        );
    }

    [Fact]
    public async Task Quota_is_reserved_when_an_upload_starts()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var alice = await SignInAsync(app, Alice);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DokumentDbContext>();
            await db.Users.ExecuteUpdateAsync(s => s.SetProperty(u => u.QuotaBytes, 1000));
        }

        var first = await CreateUploadAsync(alice, "a.bin", 600, null, null);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var second = await CreateUploadAsync(alice, "b.bin", 600, null, null);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, second.StatusCode);

        // Cancelling the first gives the space back.
        Assert.Equal(
            HttpStatusCode.NoContent,
            (
                await alice.SendAsync(Tus(HttpMethod.Delete, first.Headers.Location!.ToString()))
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Created,
            (await CreateUploadAsync(alice, "b.bin", 600, null, null)).StatusCode
        );
        var storage = await alice.GetFromJsonAsync<StorageUsage>("/api/v1/storage", Json.Options);
        Assert.Equal(new StorageUsage(0, 1000, 0), storage);
    }

    [Fact]
    public async Task Folders_report_the_size_of_what_they_hold()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var alice = await SignInAsync(app, Alice);
        var kvitton = await CreateFolderAsync(alice, null, "Kvitton");
        var year = await CreateFolderAsync(alice, kvitton.Id, "2025");
        await CreateFolderAsync(alice, null, "Empty");
        var receipt = await UploadAsync(
            alice,
            "ikea.pdf",
            RandomNumberGenerator.GetBytes(1000),
            year.Id
        );
        await UploadAsync(
            alice,
            "ikea.pdf",
            RandomNumberGenerator.GetBytes(2000),
            year.Id,
            nodeId: receipt
        );
        var note = await UploadAsync(
            alice,
            "note.txt",
            RandomNumberGenerator.GetBytes(10),
            kvitton.Id
        );
        Assert.Equal(
            HttpStatusCode.NoContent,
            (
                await alice.PostAsJsonAsync("/api/v1/nodes/trash", new NodeIdsRequest([note]))
            ).StatusCode
        );

        // Only current versions of live files count: not the first version, not the trashed note.
        var top = await ListAsync(alice, null);
        var folder = top.Items.Single(i => i.Name == "Kvitton");
        Assert.Equal(2000, folder.Size);
        Assert.Equal(new FolderCounts(1, 1), folder.Contents);
        var empty = top.Items.Single(i => i.Name == "Empty");
        Assert.Equal(0, empty.Size);
        Assert.Equal(new FolderCounts(0, 0), empty.Contents);

        var inside = await ListAsync(alice, kvitton.Id);
        Assert.Equal(2000, inside.Folder!.Size);
        var sub = Assert.Single(inside.Items);
        Assert.Equal(2000, sub.Size);
        Assert.Equal(new FolderCounts(1, 0), sub.Contents);
    }

    [Fact]
    public async Task Other_users_files_are_invisible()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var alice = await SignInAsync(app, Alice);
        using var bob = await SignInAsync(app, Bob);
        var folder = await CreateFolderAsync(alice, null, "Privat");
        var file = await UploadAsync(alice, "secret.txt", [42], folder.Id);

        Assert.Empty((await ListAsync(bob, null)).Items);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await bob.GetAsync($"/api/v1/nodes?folderId={folder.Id}")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await bob.GetAsync($"/api/v1/nodes/{file}/content")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (
                await bob.PostAsJsonAsync(
                    $"/api/v1/nodes/{file}/rename",
                    new RenameRequest("mine.txt")
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await CreateUploadAsync(bob, "x.txt", 1, folder.Id, null)).StatusCode
        );
    }

    internal static async Task<HttpClient> SignInAsync(DokumentApp app, FakeUser user)
    {
        var browser = app.CreateBrowser();
        await app.LoginAsync(browser, user);
        var session = await DokumentApp.GetSessionAsync(browser);
        browser.DefaultRequestHeaders.Add(SessionAuthentication.CsrfHeader, session.CsrfToken);
        return browser;
    }

    internal static async Task<NodeDto> CreateFolderAsync(
        HttpClient client,
        Guid? parent,
        string name
    )
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/folders",
            new CreateFolderRequest(parent, name)
        );
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<NodeDto>(Json.Options))!;
    }

    internal static async Task<FolderListing> ListAsync(HttpClient client, Guid? folder) =>
        (
            await client.GetFromJsonAsync<FolderListing>(
                folder is null ? "/api/v1/nodes" : $"/api/v1/nodes?folderId={folder}",
                Json.Options
            )
        )!;

    internal static async Task<List<VersionDto>> VersionsAsync(HttpClient client, Guid id) =>
        (
            await client.GetFromJsonAsync<List<VersionDto>>(
                $"/api/v1/nodes/{id}/versions",
                Json.Options
            )
        )!;

    internal static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static HttpRequestMessage Tus(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Tus-Resumable", TusEndpoints.Version);
        return request;
    }

    internal static Task<HttpResponseMessage> CreateUploadAsync(
        HttpClient client,
        string name,
        long length,
        Guid? folder,
        string? sha256,
        Guid? nodeId = null,
        string? onConflict = null
    )
    {
        var metadata = new Dictionary<string, string?>
        {
            ["filename"] = name,
            ["folderId"] = folder?.ToString(),
            ["nodeId"] = nodeId?.ToString(),
            ["sha256"] = sha256,
            ["onConflict"] = onConflict,
        };
        var request = Tus(HttpMethod.Post, "/api/v1/uploads");
        request.Headers.Add(
            "Upload-Length",
            length.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        request.Headers.Add(
            "Upload-Metadata",
            string.Join(
                ',',
                metadata
                    .Where(m => m.Value is not null)
                    .Select(m =>
                        $"{m.Key} {Convert.ToBase64String(Encoding.UTF8.GetBytes(m.Value!))}"
                    )
            )
        );
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PatchAsync(
        HttpClient client,
        string location,
        long offset,
        byte[] chunk
    )
    {
        var request = Tus(HttpMethod.Patch, location);
        request.Headers.Add(
            "Upload-Offset",
            offset.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        request.Content = new ByteArrayContent(chunk);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(
            TusEndpoints.OffsetContentType
        );
        return client.SendAsync(request);
    }

    /// <summary>Uploads in one chunk and returns the file's id.</summary>
    internal static async Task<Guid> UploadAsync(
        HttpClient client,
        string name,
        byte[] bytes,
        Guid? folder,
        Guid? nodeId = null,
        string? onConflict = null
    )
    {
        var create = await CreateUploadAsync(
            client,
            name,
            bytes.Length,
            folder,
            Sha256(bytes),
            nodeId,
            onConflict
        );
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var patch = await PatchAsync(client, create.Headers.Location!.ToString(), 0, bytes);
        Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);
        return Guid.Parse(patch.Headers.GetValues(TusEndpoints.NodeIdHeader).Single());
    }
}
