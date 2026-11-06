using System.Net;
using System.Net.Http.Json;
using Dokument.Domain.Audit;
using Dokument.Domain.Files;
using Dokument.Domain.Jobs;
using Dokument.Domain.Search;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Persistence;
using Dokument.Server.Files;
using Dokument.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetVips;
using static Dokument.IntegrationTests.FileTests;

namespace Dokument.IntegrationTests;

/// <summary>Merging and splitting PDFs (docs/design.md §8.4).</summary>
public sealed class PdfTests(PostgresFixture postgres)
{
    private static readonly FakeUser Alice = new(
        "alice-sub",
        "Alice Andersson",
        "alice@example.com"
    );

    private static readonly Dictionary<string, string?> FastWorker = new()
    {
        ["Worker:PollInterval"] = "00:00:00.100",
    };

    [Fact]
    public async Task Merging_makes_a_new_file_with_the_pages_and_text_of_both()
    {
        await using var app = await DokumentApp.StartAsync(postgres, FastWorker);
        using var alice = await SignInAsync(app, Alice);
        var contract = await UploadAsync(
            alice,
            "Hyreskontrakt.pdf",
            TestFiles.TextPdf("Hyreskontrakt sida ett", "Hyreskontrakt sida två"),
            null
        );
        var annex = await UploadAsync(alice, "Bilaga.pdf", TestFiles.TextPdf("Planritning"), null);
        await WaitUntilReadAsync(app, 2);
        var tagged = await alice.PostAsJsonAsync(
            "/api/v1/nodes/tags",
            new ChangeTagsRequest([annex], ["Hyra"], null)
        );
        Assert.True(tagged.IsSuccessStatusCode);

        var started = await alice.PostAsJsonAsync(
            "/api/v1/pdf/merge",
            new MergePdfsRequest([contract, annex], null, "Allt.pdf")
        );
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var op = await WaitUntilFinishedAsync(alice, (await ReadAsync(started)).Id);
        Assert.Equal(PdfOperationState.Done, op.State);
        var result = Assert.Single(op.Results);
        Assert.Equal("Allt.pdf", result.Name);
        Assert.Null(result.DuplicateOf);

        // The originals stay, and the new file has all three pages.
        var listing = await ListAsync(alice, null);
        Assert.Equal(3, listing.Items.Count);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DokumentDbContext>();
        var ct = TestContext.Current.CancellationToken;
        var node = await db
            .Nodes.Include(n => n.CurrentVersion)
            .SingleAsync(n => n.Id == result.NodeId, ct);
        Assert.Equal("application/pdf", node.CurrentVersion!.MimeType);

        // The text came from the sources, page by page, so the text stage didn't read it again.
        var pages = await db
            .ContentPages.Where(p =>
                p.BlobHash == node.CurrentVersion.BlobHash && p.VersionId == null
            )
            .OrderBy(p => p.PageNo)
            .ToListAsync(ct);
        Assert.Equal(
            ["Hyreskontrakt sida ett", "Hyreskontrakt sida två", "Planritning"],
            pages.Where(p => p.PageNo > 0).Select(p => p.Text.Trim())
        );
        Assert.False(
            await db.Jobs.AnyAsync(
                j => j.Type == JobType.Text && j.Subject == node.CurrentVersion.BlobHash.Hex,
                ct
            )
        );
        // What exiftool read comes from the first source, not from the new document PDFium wrote.
        var first = await db
            .Nodes.Where(n => n.Id == contract)
            .Select(n => n.CurrentVersion!.BlobHash)
            .SingleAsync(ct);
        var read = await db.BlobMetadata.SingleAsync(m => m.BlobHash == first, ct);
        var copied = await db.BlobMetadata.SingleAsync(
            m => m.BlobHash == node.CurrentVersion.BlobHash,
            ct
        );
        Assert.Equal(read.Tags, copied.Tags);
        Assert.False(
            await db.Jobs.AnyAsync(
                j => j.Type == JobType.Metadata && j.Subject == node.CurrentVersion.BlobHash.Hex,
                ct
            )
        );

        Assert.Equal(
            3,
            (
                await db.BlobContents.SingleAsync(
                    c => c.BlobHash == node.CurrentVersion.BlobHash,
                    ct
                )
            ).PageCount
        );

        // Search finds it by the annex's words, and it has the annex's tag.
        var withTag = await db
            .NodeTags.Where(t => t.NodeId == node.Id)
            .Select(t => t.TagKey)
            .ToListAsync(ct);
        Assert.Equal(["hyra"], withTag);

        // It says where its pages came from.
        var origin = (
            await alice.GetFromJsonAsync<FileOriginDto>(
                $"/api/v1/nodes/{node.Id}/origin",
                Json.Options,
                ct
            )
        )!;
        Assert.Equal(PdfOperationKind.Merge, origin.Kind);
        Assert.Equal(
            [("Hyreskontrakt.pdf", 1, 2), ("Bilaga.pdf", 3, 3)],
            origin.Lines.Select(l => (l.SourceName, l.First, l.Last))
        );
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await alice.GetAsync($"/api/v1/nodes/{contract}/origin", ct)).StatusCode
        );

        var audit = await db.AuditEvents.SingleAsync(e => e.TargetNodeId == node.Id, ct);
        Assert.Equal(AuditActions.PdfMerge, audit.Action);
        Assert.Equal(2, audit.Details!["sources"]!.AsArray().Count);

        // The quota held while it ran is released, and the new bytes count.
        var owner = await db.Users.SingleAsync(ct);
        Assert.Equal(0, owner.ReservedBytes);
    }

    [Fact]
    public async Task Splitting_into_a_new_folder_can_trash_the_original()
    {
        await using var app = await DokumentApp.StartAsync(postgres, FastWorker);
        using var alice = await SignInAsync(app, Alice);
        var scan = await UploadAsync(
            alice,
            "Skanning.pdf",
            TestFiles.TextPdf("Kvitto ett", "Kvitto två", "Tom sida", "Kvitto tre"),
            null
        );
        await WaitUntilReadAsync(app, 1);

        var started = await alice.PostAsJsonAsync(
            "/api/v1/pdf/split",
            new SplitPdfRequest(
                scan,
                [
                    new SplitPartRequest("Kvitto 1–2.pdf", "1-2"),
                    new SplitPartRequest("Kvitto 3.pdf", "4"),
                ],
                NewFolderName: "Skanning",
                TrashOriginal: true
            )
        );
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var op = await WaitUntilFinishedAsync(alice, (await ReadAsync(started)).Id);
        Assert.Equal(PdfOperationState.Done, op.State);
        Assert.Equal(["Kvitto 1–2.pdf", "Kvitto 3.pdf"], op.Results.Select(r => r.Name));

        // The parts are in the new folder, and the original is in the trash.
        var top = await ListAsync(alice, null);
        var folder = Assert.Single(top.Items);
        Assert.Equal("Skanning", folder.Name);
        Assert.Equal(NodeKind.Folder, folder.Kind);
        var inside = await ListAsync(alice, folder.Id);
        Assert.Equal(["Kvitto 1–2.pdf", "Kvitto 3.pdf"], inside.Items.Select(i => i.Name).Order());

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DokumentDbContext>();
        var ct = TestContext.Current.CancellationToken;
        Assert.NotNull((await db.Nodes.SingleAsync(n => n.Id == scan, ct)).TrashedAt);
        var last = await db
            .Nodes.Include(n => n.CurrentVersion)
            .SingleAsync(n => n.Id == op.Results[1].NodeId, ct);
        var text = await db
            .ContentPages.Where(p => p.BlobHash == last.CurrentVersion!.BlobHash && p.PageNo == 1)
            .SingleAsync(ct);
        Assert.Equal("Kvitto tre", text.Text.Trim());

        // Once the worker has looked at it, the part says it has one page.
        await WaitUntilAsync(async () =>
            (await ListAsync(alice, folder.Id)).Items.Single(i => i.Id == last.Id).Pdf?.PageCount
            == 1
        );
    }

    [Fact]
    public async Task Only_readable_PDFs_with_pages_in_range_are_taken()
    {
        await using var app = await DokumentApp.StartAsync(postgres, FastWorker);
        using var alice = await SignInAsync(app, Alice);
        var pdf = await UploadAsync(alice, "a.pdf", TestFiles.TextPdf("Ett", "Två"), null);
        var photo = await UploadAsync(alice, "b.png", TestFiles.Png(40, 30), null);
        var broken = await UploadAsync(alice, "c.pdf", "%PDF-1.4 not really"u8.ToArray(), null);
        await WaitUntilReadAsync(app, 3);

        var listing = (await ListAsync(alice, null)).Items.ToDictionary(i => i.Id);
        Assert.Equal(new PdfDto(2, true), listing[pdf].Pdf);
        Assert.Equal(new PdfDto(null, false), listing[broken].Pdf);
        Assert.Null(listing[photo].Pdf);

        async Task<HttpStatusCode> MergeAsync(params Guid[] ids) =>
            (
                await alice.PostAsJsonAsync(
                    "/api/v1/pdf/merge",
                    new MergePdfsRequest([.. ids], null, "x.pdf")
                )
            ).StatusCode;
        Assert.Equal(HttpStatusCode.BadRequest, await MergeAsync(pdf));
        Assert.Equal(HttpStatusCode.BadRequest, await MergeAsync(pdf, photo));
        Assert.Equal(HttpStatusCode.BadRequest, await MergeAsync(pdf, broken));
        Assert.Equal(HttpStatusCode.BadRequest, await MergeAsync(pdf, pdf));

        var outside = await alice.PostAsJsonAsync(
            "/api/v1/pdf/split",
            new SplitPdfRequest(pdf, [new SplitPartRequest("y.pdf", "2-3")])
        );
        Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);
        Assert.Contains("outside pages 1 to 2", await outside.Content.ReadAsStringAsync());

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DokumentDbContext>();
        Assert.False(await db.PdfOperations.AnyAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            0,
            (await db.Users.SingleAsync(TestContext.Current.CancellationToken)).ReservedBytes
        );
    }

    [Fact]
    public async Task Page_sheets_are_made_when_asked_for()
    {
        await using var app = await DokumentApp.StartAsync(postgres, FastWorker);
        using var alice = await SignInAsync(app, Alice);
        var pdf = await UploadAsync(alice, "a.pdf", TestFiles.TextPdf("Ett", "Två", "Tre"), null);
        await WaitUntilReadAsync(app, 1);

        PageSheetsDto sheets = null!;
        await WaitUntilAsync(async () =>
        {
            var response = await alice.PostAsync($"/api/v1/nodes/{pdf}/page-sheets", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            sheets = (await response.Content.ReadFromJsonAsync<PageSheetsDto>(Json.Options))!;
            return sheets.Ready;
        });
        Assert.Equal(3, sheets.PageCount);
        Assert.Equal(1, sheets.Sheets);

        var image = await alice.GetAsync($"/api/v1/nodes/{pdf}/page-sheets/1");
        Assert.Equal("image/webp", image.Content.Headers.ContentType!.MediaType);
        using (var sheet = Image.NewFromBuffer(await image.Content.ReadAsByteArrayAsync()))
        {
            Assert.Equal((3 * sheets.CellWidth, sheets.CellHeight), (sheet.Width, sheet.Height));
        }

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await alice.GetAsync($"/api/v1/nodes/{pdf}/page-sheets/2")).StatusCode
        );
    }

    private static async Task<PdfOperationDto> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<PdfOperationDto>(Json.Options))!;

    private static async Task<PdfOperationDto> WaitUntilFinishedAsync(HttpClient client, Guid id)
    {
        PdfOperationDto op = null!;
        await WaitUntilAsync(async () =>
        {
            op = (
                await client.GetFromJsonAsync<PdfOperationDto>(
                    $"/api/v1/pdf/operations/{id}",
                    Json.Options
                )
            )!;
            return op.State
                is not (
                    PdfOperationState.Waiting
                    or PdfOperationState.Working
                    or PdfOperationState.Saving
                );
        });
        return op;
    }

    /// <summary>Waits until the thumbnail, metadata and text stages have run for <paramref name="files"/> blobs.</summary>
    private static Task WaitUntilReadAsync(DokumentApp app, int files) =>
        WaitUntilAsync(async () =>
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DokumentDbContext>();
            return await db.BlobPreviews.CountAsync() >= files
                && !await db.Jobs.AnyAsync(j =>
                    (
                        j.Type == JobType.Thumbnail
                        || j.Type == JobType.Metadata
                        || j.Type == JobType.Text
                    )
                    && j.Status != JobStatus.Done
                    && j.Status != JobStatus.Failed
                );
        });

    private static async Task WaitUntilAsync(Func<Task<bool>> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await done())
        {
            Assert.True(DateTime.UtcNow < deadline, "Still not done after 30 s.");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}
