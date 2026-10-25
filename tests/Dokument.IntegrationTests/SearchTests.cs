using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Dokument.Domain.Jobs;
using Dokument.Domain.Search;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Search;
using Dokument.Server.Admin;
using Dokument.Server.Files;
using Dokument.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Dokument.IntegrationTests.FileTests;

namespace Dokument.IntegrationTests;

public sealed class SearchTests(PostgresFixture postgres)
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

    private static readonly Dictionary<string, string?> FastWorker = new()
    {
        ["Worker:PollInterval"] = "00:00:00.100",
    };

    [Fact]
    public async Task Text_in_PDFs_Office_files_and_scans_is_found()
    {
        await using var app = await DokumentApp.StartAsync(postgres, FastWorker);
        using var alice = await SignInAsync(app, Alice);
        var receipts = await CreateFolderAsync(alice, null, "Inköp 2025");
        var pdf = await UploadAsync(
            alice,
            "inköp.pdf",
            TestFiles.TextPdf("Sammanfattning", "Kvittot från IKEA\nSoffa 7995 kr"),
            receipts.Id
        );
        var docx = await UploadAsync(
            alice,
            "lägenhet.docx",
            TestFiles.Docx("Hyresavtal för lägenheten", "Hyran betalas i förskott."),
            null
        );
        var slides = await UploadAsync(
            alice,
            "möte.pptx",
            TestFiles.Pptx("Agenda", "Budget för renoveringen"),
            null
        );
        var scan = await UploadAsync(
            alice,
            "skanning.png",
            TestFiles.TextImage("Apotek Hjärtat\nTotal 249 kr\nTack för besöket"),
            null
        );
        await WaitForWorkerAsync(app);

        // A word in the second page of a PDF, with the best page and the hit marked in the snippet.
        var hit = Assert.Single((await SearchAsync(alice, "kvittot")).Results);
        Assert.Equal(pdf, hit.Node.Id);
        Assert.Equal(MatchSource.Text, hit.Source);
        Assert.Equal((2, 2), (hit.PageNo, hit.PageCount));
        Assert.Equal(["Inköp 2025"], hit.Path.Select(p => p.Name));
        var mark = Assert.Single(hit.Snippet!.Highlights);
        Assert.Equal("Kvittot", hit.Snippet.Text[mark[0]..mark[1]]);

        // Swedish stemming: "hyresavtalen" finds "Hyresavtal".
        Assert.Equal(
            docx,
            Assert.Single((await SearchAsync(alice, "hyresavtalen")).Results).Node.Id
        );
        var slide = Assert.Single((await SearchAsync(alice, "budget")).Results);
        Assert.Equal((slides, 2), (slide.Node.Id, slide.PageNo));

        // Recognized text in a picture, with boxes for the viewer to mark.
        var found = Assert.Single((await SearchAsync(alice, "apotek")).Results);
        Assert.Equal((scan, MatchSource.Ocr), (found.Node.Id, found.Source));
        var matches = await alice.GetFromJsonAsync<MatchesResponse>(
            $"/api/v1/nodes/{scan}/matches?q=apotek",
            Json.Options,
            TestContext.Current.CancellationToken
        );
        var page = Assert.Single(matches!.Pages);
        Assert.Equal(PageSource.Ocr, page.Source);
        Assert.NotEmpty(page.Boxes);
        Assert.All(page.Boxes, b => Assert.InRange(b[0], 0, 1));

        // Both words must match, filters narrow, and a phrase must be in order.
        Assert.Empty((await SearchAsync(alice, "kvittot hyresavtal")).Results);
        Assert.Single((await SearchAsync(alice, "kvittot", "&type=pdf")).Results);
        var none = await SearchAsync(alice, "kvittot", "&type=image");
        Assert.Empty(none.Results);
        Assert.Equal(1, none.WithoutFilters);
        Assert.Single((await SearchAsync(alice, "\"soffa 7995\"")).Results);
        Assert.Empty((await SearchAsync(alice, "\"7995 soffa\"")).Results);
        Assert.Single((await SearchAsync(alice, "kvittot", $"&folderId={receipts.Id}")).Results);
        Assert.Empty((await SearchAsync(alice, "hyresavtal in:inköp")).Results);
    }

    [Fact]
    public async Task Names_match_partly_and_with_typos()
    {
        await using var app = await DokumentApp.StartAsync(postgres, FastWorker);
        using var alice = await SignInAsync(app, Alice);
        var photo = await UploadAsync(alice, "IMG_4471.jpg", TestFiles.Png(64, 64), null);
        var folder = await CreateFolderAsync(alice, null, "Deklarationer");
        await WaitForWorkerAsync(app);

        Assert.Equal(photo, Assert.Single((await SearchAsync(alice, "4471")).Results).Node.Id);
        Assert.Equal(
            folder.Id,
            Assert.Single((await SearchAsync(alice, "deklaration")).Results).Node.Id
        );
        Assert.Equal(
            folder.Id,
            Assert.Single((await SearchAsync(alice, "deklaratoiner")).Results).Node.Id
        );

        var suggest = await alice.GetFromJsonAsync<SuggestResponse>(
            "/api/v1/search/suggest?q=dekl",
            Json.Options,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(folder.Id, Assert.Single(suggest!.Items).Node.Id);
        Assert.Equal(["dekl"], suggest.Terms);
    }

    [Fact]
    public async Task Camera_and_date_taken_are_searchable()
    {
        await using var app = await DokumentApp.StartAsync(postgres, FastWorker);
        using var alice = await SignInAsync(app, Alice);
        var photo = await UploadAsync(
            alice,
            "semester.jpg",
            await PhotoAsync("FUJIFILM", "X-T30", "2024:06:14 10:00:00"),
            null
        );
        await UploadAsync(
            alice,
            "annan.jpg",
            await PhotoAsync("Apple", "iPhone 15", "2025:01:02 09:00:00"),
            null
        );
        await WaitForWorkerAsync(app);

        var hit = Assert.Single((await SearchAsync(alice, "fujifilm")).Results);
        Assert.Equal((photo, MatchSource.Metadata), (hit.Node.Id, hit.Source));
        Assert.Equal(
            photo,
            Assert.Single((await SearchAsync(alice, "camera:fujifilm")).Results).Node.Id
        );
        Assert.Equal(
            photo,
            Assert.Single((await SearchAsync(alice, "taken:2024")).Results).Node.Id
        );
        Assert.Equal(
            photo,
            Assert.Single((await SearchAsync(alice, "taken:2024-06")).Results).Node.Id
        );
        Assert.Empty((await SearchAsync(alice, "taken:2023")).Results);
    }

    [Fact]
    public async Task Only_files_the_person_can_open_are_found()
    {
        await using var app = await DokumentApp.StartAsync(postgres, FastWorker);
        using var alice = await SignInAsync(app, Alice);
        using var bob = await SignInAsync(app, Bob);
        var mine = await UploadAsync(
            alice,
            "lön.pdf",
            TestFiles.TextPdf("Lönespecifikation oktober"),
            null
        );
        var trashed = await UploadAsync(
            alice,
            "gammal.pdf",
            TestFiles.TextPdf("Lönespecifikation september"),
            null
        );
        await WaitForWorkerAsync(app);

        Assert.Equal(2, (await SearchAsync(alice, "lönespecifikation")).Total);
        Assert.Empty((await SearchAsync(bob, "lönespecifikation")).Results);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await bob.GetAsync($"/api/v1/nodes/{mine}/matches?q=lönespecifikation")).StatusCode
        );

        await alice.PostAsJsonAsync("/api/v1/nodes/trash", new NodeIdsRequest([trashed]));
        Assert.Equal(
            mine,
            Assert.Single((await SearchAsync(alice, "lönespecifikation")).Results).Node.Id
        );
    }

    [Fact]
    public async Task Scans_skipped_while_text_recognition_is_off_are_read_when_it_is_turned_on()
    {
        await using var app = await DokumentApp.StartAsync(postgres, FastWorker);
        using var admin = await SignInAsync(app, Admin);
        using var alice = await SignInAsync(app, Alice);
        await admin.PutAsJsonAsync(
            "/api/v1/admin/settings/text-recognition",
            new TextRecognitionDto(false),
            TestContext.Current.CancellationToken
        );
        var scan = await UploadAsync(
            alice,
            "skanning.png",
            TestFiles.TextImage("Elräkning för november\nAtt betala 812 kr"),
            null
        );
        await WaitForWorkerAsync(app);
        Assert.Empty((await SearchAsync(alice, "elräkning")).Results);

        await admin.PutAsJsonAsync(
            "/api/v1/admin/settings/text-recognition",
            new TextRecognitionDto(true),
            TestContext.Current.CancellationToken
        );
        await WaitForWorkerAsync(app);
        Assert.Equal(scan, Assert.Single((await SearchAsync(alice, "elräkning")).Results).Node.Id);

        var progress = await admin.GetFromJsonAsync<TextProgress>(
            "/api/v1/admin/text-recognition/progress",
            Json.Options,
            TestContext.Current.CancellationToken
        );
        Assert.Equal((1, 0), (progress!.Read, progress.Waiting));
    }

    [Fact]
    public async Task Queries_with_only_filters_list_matching_files()
    {
        await using var app = await DokumentApp.StartAsync(postgres, FastWorker);
        using var alice = await SignInAsync(app, Alice);
        var pdf = await UploadAsync(alice, "a.pdf", TestFiles.TextPdf("Hej"), null);
        await UploadAsync(alice, "b.png", TestFiles.Png(32, 32), null);
        await WaitForWorkerAsync(app);

        Assert.Equal(pdf, Assert.Single((await SearchAsync(alice, "type:pdf")).Results).Node.Id);
        var bad = await alice.GetAsync(
            "/api/v1/search?q=x&type=nonsense",
            TestContext.Current.CancellationToken
        );
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    private static async Task<SearchResponse> SearchAsync(
        HttpClient client,
        string q,
        string extra = ""
    ) =>
        (
            await client.GetFromJsonAsync<SearchResponse>(
                $"/api/v1/search?q={Uri.EscapeDataString(q)}{extra}",
                Json.Options,
                TestContext.Current.CancellationToken
            )
        )!;

    /// <summary>Waits until the worker has read every file's text and metadata.</summary>
    private static async Task WaitForWorkerAsync(DokumentApp app)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DokumentDbContext>();
            var jobs = await db.Jobs.ToListAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(jobs, j => j.Status == JobStatus.Failed);
            if (jobs.All(j => j.Status == JobStatus.Done))
            {
                return;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Fail("The worker didn't finish in time.");
    }

    /// <summary>A small JPEG with camera and date taken written by exiftool, as a camera would.</summary>
    private static async Task<byte[]> PhotoAsync(string make, string model, string taken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.jpg");
        try
        {
            using (var image = NetVips.Image.Black(64, 48, bands: 3))
            {
                image.Jpegsave(path);
            }

            using var process = Process.Start(
                new ProcessStartInfo("exiftool")
                {
                    ArgumentList =
                    {
                        "-overwrite_original",
                        $"-Make={make}",
                        $"-Model={model}",
                        $"-DateTimeOriginal={taken}",
                        path,
                    },
                    RedirectStandardOutput = true,
                }
            )!;
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, process.ExitCode);
            return await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
