using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Dokument.Domain.Jobs;
using Dokument.Domain.Search;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Search;
using Dokument.Server.Files;
using Dokument.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Dokument.IntegrationTests.FileTests;

namespace Dokument.IntegrationTests;

/// <summary>The Details and Text sections of a file's info, and the edits people make there (docs/design.md §13.5).</summary>
public sealed class FileDetailsTests(PostgresFixture postgres)
{
    private static readonly FakeUser Alice = new(
        "alice-sub",
        "Alice Andersson",
        "alice@example.com"
    );
    private static readonly FakeUser Bob = new("bob-sub", "Bob Berg", "bob@example.com");

    private static readonly Dictionary<string, string?> FastWorker = new()
    {
        ["Worker:PollInterval"] = "00:00:00.100",
    };

    private const string ReceiptText = "Apotek Hjärtat\nTotal 249 kr\nTack för besöket";

    [Fact]
    public async Task Details_show_what_the_file_says_and_edits_win_for_search_and_later_versions()
    {
        await using var app = await DokumentApp.StartAsync(postgres, FastWorker);
        using var alice = await SignInAsync(app, Alice);
        using var bob = await SignInAsync(app, Bob);
        var photo = await UploadAsync(
            alice,
            "skanning.jpg",
            await PhotoAsync(
                "-Make=EPSON",
                "-Model=Perfection V600",
                "-DateTimeOriginal=2026:03:14 10:02:30",
                "-OffsetTimeOriginal=+01:00",
                "-XMP:Title=Brygga",
                "-Artist=Okänd fotograf",
                "-GPSLatitude=59.3157",
                "-GPSLatitudeRef=N",
                "-GPSLongitude=18.0335",
                "-GPSLongitudeRef=E"
            ),
            null
        );
        await WaitForWorkerAsync(app);

        var read = await DetailsAsync(alice, photo);
        Assert.False(read.Reading);
        Assert.Equal(("Brygga", false), (read.Title.Value, read.Title.Edited));
        Assert.Equal("EPSON Perfection V600", read.Camera);
        Assert.Equal((64, 48), (read.Width, read.Height));
        var taken = read.Taken.Value!;
        Assert.Equal(
            ("2026-03-14 10:02", DatePrecision.Time, 60),
            (taken.Value, taken.Precision, taken.OffsetMinutes)
        );
        Assert.Equal(59.3157, read.Place!.Latitude, 3);
        Assert.Equal(18.0335, read.Place.Longitude, 3);
        Assert.Contains(new DetailField("artist", "Okänd fotograf"), read.Fields);

        // The scan is of an old photo: a better title, a description and a date known only to the month.
        var save = await alice.PutAsJsonAsync(
            $"/api/v1/nodes/{photo}/details",
            new FileDetailsRequest("Farmor på bryggan", "Sommaren i Grisslehamn", "1974-07"),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(HttpStatusCode.NoContent, save.StatusCode);
        var edited = await DetailsAsync(alice, photo);
        Assert.Equal(
            ("Farmor på bryggan", "Brygga", true),
            (edited.Title.Value, edited.Title.Read, edited.Title.Edited)
        );
        Assert.Equal(
            ("1974-07", DatePrecision.Month),
            (edited.Taken.Value!.Value, edited.Taken.Value.Precision)
        );
        Assert.Equal("2026-03-14 10:02", edited.Taken.Read!.Value);
        Assert.Equal("Alice Andersson", edited.EditedBy);

        // Search uses the edits: the words, and the date for taken:.
        Assert.Equal(
            photo,
            Assert.Single((await SearchAsync(alice, "grisslehamn")).Results).Node.Id
        );
        Assert.Equal(
            photo,
            Assert.Single((await SearchAsync(alice, "taken:1974")).Results).Node.Id
        );
        Assert.Empty((await SearchAsync(alice, "taken:2026")).Results);

        // Bad dates are refused, and nobody else can see or edit them.
        var bad = await alice.PutAsJsonAsync(
            $"/api/v1/nodes/{photo}/details",
            new FileDetailsRequest(null, null, "juli 1974"),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await bob.GetAsync($"/api/v1/nodes/{photo}/details")).StatusCode
        );
        var bobs = await bob.PutAsJsonAsync(
            $"/api/v1/nodes/{photo}/details",
            new FileDetailsRequest("Bob", null, null),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(HttpStatusCode.NotFound, bobs.StatusCode);

        // A new version, such as a better scan, starts with the edits.
        await UploadAsync(alice, "skanning.jpg", await PhotoAsync("-Make=EPSON"), null, photo);
        await WaitForWorkerAsync(app);
        var next = await DetailsAsync(alice, photo);
        Assert.Equal(("Farmor på bryggan", true), (next.Title.Value, next.Title.Edited));
        Assert.Equal("1974-07", next.Taken.Value!.Value);

        // Saving what the file says, or nothing, removes the edits.
        await alice.PutAsJsonAsync(
            $"/api/v1/nodes/{photo}/details",
            new FileDetailsRequest(null, null, null),
            TestContext.Current.CancellationToken
        );
        var cleared = await DetailsAsync(alice, photo);
        Assert.False(cleared.Title.Edited || cleared.Description.Edited || cleared.Taken.Edited);
        Assert.Empty((await SearchAsync(alice, "grisslehamn")).Results);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DokumentDbContext>();
        Assert.Contains(
            await db
                .AuditEvents.Select(a => a.Action)
                .ToListAsync(TestContext.Current.CancellationToken),
            a => a == "file.details_edit"
        );
    }

    [Fact]
    public async Task Text_corrections_are_searched_belong_to_the_version_and_stay_private_to_the_file()
    {
        await using var app = await DokumentApp.StartAsync(postgres, FastWorker);
        using var alice = await SignInAsync(app, Alice);
        using var bob = await SignInAsync(app, Bob);
        var scan = await UploadAsync(alice, "kvitto.png", TestFiles.TextImage(ReceiptText), null);
        var bobsCopy = await UploadAsync(bob, "kvitto.png", TestFiles.TextImage(ReceiptText), null);
        await WaitForWorkerAsync(app);

        var text = await TextAsync(alice, scan);
        Assert.Equal((TextState.Ready, 1, 0), (text.State, text.TotalPages, text.EditedPages));
        var page = Assert.Single(text.Pages);
        Assert.Equal((1, PageSource.Ocr, false), (page.PageNo, page.Source, page.Edited));
        Assert.Contains("Apotek", page.Text);

        // Fix the recognized text: "besöket" goes, "medicin" comes.
        var fixedText = "Apotek Hjärtat\nTotal 249 kr\nMedicin";
        var put = await alice.PutAsJsonAsync(
            $"/api/v1/nodes/{scan}/text/1",
            new FileTextPageRequest(fixedText),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var corrected = await TextAsync(alice, scan);
        Assert.Equal(
            (fixedText, true, "Alice Andersson"),
            (corrected.Pages[0].Text, corrected.Pages[0].Edited, corrected.Pages[0].EditedBy)
        );
        Assert.Equal(1, corrected.EditedPages);
        Assert.Equal(6, corrected.Words);

        var hit = Assert.Single((await SearchAsync(alice, "medicin")).Results);
        Assert.Equal((scan, MatchSource.Ocr), (hit.Node.Id, hit.Source));
        Assert.Empty((await SearchAsync(alice, "besöket")).Results);

        // The viewer still marks words the scan has, from what was read.
        var matches = await alice.GetFromJsonAsync<MatchesResponse>(
            $"/api/v1/nodes/{scan}/matches?q=apotek",
            Json.Options,
            TestContext.Current.CancellationToken
        );
        Assert.NotEmpty(Assert.Single(matches!.Pages).Boxes);

        // So does the Text toggle: every word that was read, with its box.
        var words = await alice.GetFromJsonAsync<RecognizedWordsDto>(
            $"/api/v1/nodes/{scan}/words?page=1",
            Json.Options,
            TestContext.Current.CancellationToken
        );
        Assert.Equal([1], words!.Pages);
        Assert.Contains(words.Words, w => w.Text == "Apotek");
        Assert.All(
            words.Words,
            w => Assert.True(w is { X: >= 0 and < 1, Y: >= 0 and < 1, Width: > 0, Height: > 0 })
        );
        var none = await alice.GetFromJsonAsync<RecognizedWordsDto>(
            $"/api/v1/nodes/{scan}/words?page=2",
            Json.Options,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(([1], 0), (none!.Pages, none.Words.Count));

        // Bob has the same bytes, but Alice's correction is hers.
        Assert.False(Assert.Single((await TextAsync(bob, bobsCopy)).Pages).Edited);
        Assert.Empty((await SearchAsync(bob, "medicin")).Results);
        Assert.Equal(bobsCopy, Assert.Single((await SearchAsync(bob, "besöket")).Results).Node.Id);

        // New bytes have new text, so the correction stays with the old version and comes back with it.
        await UploadAsync(
            alice,
            "kvitto.png",
            TestFiles.TextImage("Apotek Hjärtat\nTotal 99 kr"),
            null,
            scan
        );
        await WaitForWorkerAsync(app);
        Assert.False(Assert.Single((await TextAsync(alice, scan)).Pages).Edited);
        Assert.Empty((await SearchAsync(alice, "medicin")).Results);
        var first = (await VersionsAsync(alice, scan)).Single(v => v.Number == 1);
        await alice.PostAsync(
            $"/api/v1/nodes/{scan}/versions/{first.Id}/restore",
            null,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(fixedText, Assert.Single((await TextAsync(alice, scan)).Pages).Text);

        // Undoing the correction brings back what was read.
        var revert = await alice.DeleteAsync(
            $"/api/v1/nodes/{scan}/text/1",
            TestContext.Current.CancellationToken
        );
        Assert.Equal(HttpStatusCode.NoContent, revert.StatusCode);
        Assert.False(Assert.Single((await TextAsync(alice, scan)).Pages).Edited);
        Assert.Single((await SearchAsync(alice, "besöket")).Results);
    }

    [Fact]
    public async Task Text_can_be_added_where_nothing_was_read()
    {
        await using var app = await DokumentApp.StartAsync(postgres, FastWorker);
        using var alice = await SignInAsync(app, Alice);
        var photo = await UploadAsync(alice, "solnedgång.png", TestFiles.Png(320, 240), null);
        await WaitForWorkerAsync(app);
        var nothing = await TextAsync(alice, photo);
        Assert.Equal((TextState.Empty, 0, 1), (nothing.State, nothing.TotalPages, nothing.MaxPage));

        await alice.PutAsJsonAsync(
            $"/api/v1/nodes/{photo}/text/1",
            new FileTextPageRequest("Solnedgång över Ålands hav"),
            TestContext.Current.CancellationToken
        );
        var text = await TextAsync(alice, photo);
        Assert.Equal(TextState.Ready, text.State);
        Assert.Equal((PageSource.Added, true), (text.Pages[0].Source, text.Pages[0].Edited));
        var hit = Assert.Single((await SearchAsync(alice, "ålands")).Results);
        Assert.Equal((photo, MatchSource.Text), (hit.Node.Id, hit.Source));

        var outside = await alice.PutAsJsonAsync(
            $"/api/v1/nodes/{photo}/text/2",
            new FileTextPageRequest("Nej"),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);
    }

    private static async Task<FileDetailsDto> DetailsAsync(HttpClient client, Guid id) =>
        (
            await client.GetFromJsonAsync<FileDetailsDto>(
                $"/api/v1/nodes/{id}/details",
                Json.Options,
                TestContext.Current.CancellationToken
            )
        )!;

    private static async Task<FileTextDto> TextAsync(HttpClient client, Guid id) =>
        (
            await client.GetFromJsonAsync<FileTextDto>(
                $"/api/v1/nodes/{id}/text",
                Json.Options,
                TestContext.Current.CancellationToken
            )
        )!;

    private static async Task<SearchResponse> SearchAsync(HttpClient client, string q) =>
        (
            await client.GetFromJsonAsync<SearchResponse>(
                $"/api/v1/search?q={Uri.EscapeDataString(q)}",
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

    /// <summary>A small JPEG with tags written by exiftool, as a camera or scanner would.</summary>
    private static async Task<byte[]> PhotoAsync(params string[] tags)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.jpg");
        try
        {
            using (var image = NetVips.Image.Black(64, 48, bands: 3))
            {
                image.Jpegsave(path);
            }

            var start = new ProcessStartInfo("exiftool") { RedirectStandardOutput = true };
            start.ArgumentList.Add("-overwrite_original");
            foreach (var tag in tags)
            {
                start.ArgumentList.Add(tag);
            }

            start.ArgumentList.Add(path);
            using var process = Process.Start(start)!;
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
