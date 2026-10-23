using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Dokument.Domain.Audit;
using Dokument.Domain.Files;
using Dokument.Domain.Jobs;
using Dokument.Domain.Previews;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Previews;
using Dokument.Server.Files;
using Dokument.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetVips;
using static Dokument.IntegrationTests.FileTests;

namespace Dokument.IntegrationTests;

public sealed class PreviewTests(PostgresFixture postgres)
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
    public async Task The_worker_makes_thumbnails_for_images_and_PDFs()
    {
        await using var app = await DokumentApp.StartAsync(postgres, FastWorker);
        using var alice = await SignInAsync(app, Alice);
        var photo = await UploadAsync(alice, "kvitto.png", TestFiles.Png(1200, 900), null);
        var pdf = await UploadAsync(alice, "avtal.pdf", TestFiles.Pdf(pages: 2), null);
        var zip = await UploadAsync(
            alice,
            "backup.zip",
            [0x50, 0x4B, 0x05, 0x06, .. new byte[18]],
            null
        );

        var listing = await WaitUntilSettledAsync(alice);
        Assert.Equal(ThumbnailState.Ready, listing[photo].Thumbnail);
        Assert.Equal(ThumbnailState.Ready, listing[pdf].Thumbnail);
        Assert.Equal(ThumbnailState.None, listing[zip].Thumbnail);

        var medium = await alice.GetAsync($"/api/v1/nodes/{photo}/thumbnail");
        Assert.Equal(HttpStatusCode.OK, medium.StatusCode);
        Assert.Equal("image/webp", medium.Content.Headers.ContentType!.MediaType);
        using (var image = Image.NewFromBuffer(await medium.Content.ReadAsByteArrayAsync()))
        {
            Assert.Equal((640, 480), (image.Width, image.Height));
        }

        var small = await alice.GetByteArrayAsync($"/api/v1/nodes/{pdf}/thumbnail?size=small");
        using (var image = Image.NewFromBuffer(small))
        {
            Assert.Equal(256, image.Height);
        }

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await alice.GetAsync($"/api/v1/nodes/{zip}/thumbnail")).StatusCode
        );

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DokumentDbContext>();
        var previews = await db.BlobPreviews.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains(previews, p => p.PageCount == 2 && p.Status == PreviewStatus.Ready);
        Assert.Contains(previews, p => p.Status == PreviewStatus.Unsupported);
        Assert.All(
            await db.Jobs.ToListAsync(TestContext.Current.CancellationToken),
            j => Assert.Equal(JobStatus.Done, j.Status)
        );
    }

    [Fact]
    public async Task Previews_are_served_inline_and_not_audited()
    {
        await using var app = await DokumentApp.StartAsync(postgres, FastWorker);
        using var alice = await SignInAsync(app, Alice);
        var png = TestFiles.Png(300, 200);
        var photo = await UploadAsync(alice, "kvitto.png", png, null);
        using var tiffImage = TestFiles.Gradient(300, 200);
        var tiff = await UploadAsync(alice, "skanning.tiff", tiffImage.TiffsaveBuffer(), null);
        var page = await UploadAsync(
            alice,
            "sida.html",
            "<script>alert(1)</script>"u8.ToArray(),
            null
        );
        await WaitUntilSettledAsync(alice);

        // A PNG is shown as it is: the original bytes, inline, with range support for big files.
        var original = await alice.GetAsync($"/api/v1/nodes/{photo}/preview");
        Assert.Equal("image/png", original.Content.Headers.ContentType!.MediaType);
        Assert.Equal("inline", original.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal(png, await original.Content.ReadAsByteArrayAsync());
        var ranged = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/nodes/{photo}/preview");
        ranged.Headers.Range = new RangeHeaderValue(0, 9);
        Assert.Equal(HttpStatusCode.PartialContent, (await alice.SendAsync(ranged)).StatusCode);

        // Browsers can't show TIFF, so the viewer gets the WebP preview.
        var converted = await alice.GetAsync($"/api/v1/nodes/{tiff}/preview");
        Assert.Equal("image/webp", converted.Content.Headers.ContentType!.MediaType);

        // Markup is previewed as its source text, sandboxed.
        var html = await alice.GetAsync($"/api/v1/nodes/{page}/preview");
        Assert.Equal("text/plain", html.Content.Headers.ContentType!.MediaType);
        Assert.Contains("sandbox", html.Headers.GetValues("Content-Security-Policy").Single());

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DokumentDbContext>();
        var downloads = await db.AuditEvents.CountAsync(
            e => e.Action == AuditActions.FileDownload,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(0, downloads);
    }

    [Fact]
    public async Task Identical_content_is_rendered_once()
    {
        await using var app = await DokumentApp.StartAsync(postgres, FastWorker);
        using var alice = await SignInAsync(app, Alice);
        var png = TestFiles.Png(400, 300);
        var first = await UploadAsync(alice, "a.png", png, null);
        await WaitUntilSettledAsync(alice);

        var copy = await alice.PostAsJsonAsync(
            "/api/v1/uploads/copy",
            new CopyFromContentRequest(null, "b.png", Sha256(png), null)
        );
        var copied = (await copy.Content.ReadFromJsonAsync<NodeDto>(Json.Options))!;
        Assert.Equal(ThumbnailState.Ready, copied.Thumbnail);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DokumentDbContext>();
        Assert.Equal(1, await db.Jobs.CountAsync(TestContext.Current.CancellationToken));
        Assert.NotEqual(first, copied.Id);
    }

    [Fact]
    public async Task Existing_files_without_thumbnails_are_backfilled()
    {
        await using var app = await DokumentApp.StartAsync(postgres, FastWorker);
        using var alice = await SignInAsync(app, Alice);
        var photo = await UploadAsync(alice, "gammal.png", TestFiles.Png(400, 300), null);
        await WaitUntilSettledAsync(alice);

        // As if the file had been uploaded before thumbnails existed.
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DokumentDbContext>();
        await db.Renditions.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await db.BlobPreviews.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await db.Jobs.ExecuteDeleteAsync(TestContext.Current.CancellationToken);

        var previews = scope.ServiceProvider.GetRequiredService<PreviewService>();
        Assert.Equal(
            1,
            await previews.EnqueueMissingAsync(
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal(
            0,
            await previews.EnqueueMissingAsync(
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken
            )
        );
        var job = await db.Jobs.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JobPriority.Background, job.Priority);

        var listing = await WaitUntilSettledAsync(alice);
        Assert.Equal(ThumbnailState.Ready, listing[photo].Thumbnail);
    }

    /// <summary>Polls the top of My files, as the web grid does, until no thumbnail is pending.</summary>
    [Fact]
    public async Task A_thumbnail_job_links_back_to_the_upload_that_queued_it()
    {
        // What an OpenTelemetry SDK would see (docs/design.md §22.1), without an exporter.
        var finished = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "Dokument" or "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = finished.Add,
        };
        ActivitySource.AddActivityListener(listener);

        await using var app = await DokumentApp.StartAsync(postgres, FastWorker);
        using var alice = await SignInAsync(app, Alice);
        var photo = await UploadAsync(alice, "spår.png", TestFiles.Png(300, 200), null);
        await WaitUntilSettledAsync(alice);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DokumentDbContext>();
        var version = await db.FileVersions.SingleAsync(
            v => v.NodeId == photo,
            TestContext.Current.CancellationToken
        );
        var job = await db.Jobs.SingleAsync(
            j => j.Subject == version.BlobHash.Hex,
            TestContext.Current.CancellationToken
        );
        Assert.True(ActivityContext.TryParse(job.TraceParent, null, out var queuedBy));

        var upload = Assert.Single(
            finished,
            a =>
                a.OperationName == "upload finish"
                && Equals(a.GetTagItem("dokument.node.id"), photo)
        );
        Assert.Equal(upload.TraceId, queuedBy.TraceId);
        var run = Assert.Single(
            finished,
            a => a.OperationName == "job thumbnail" && Equals(a.GetTagItem("job.id"), job.Id)
        );
        Assert.Equal(queuedBy, Assert.Single(run.Links).Context);
        Assert.Equal("succeeded", run.GetTagItem("job.outcome"));
        Assert.Contains(
            finished,
            a => a.OperationName == "thumbnail render" && a.TraceId == run.TraceId
        );
    }

    private static async Task<Dictionary<Guid, NodeDto>> WaitUntilSettledAsync(HttpClient client)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var listing = await ListAsync(client, null);
            if (listing.Items.All(i => i.Thumbnail != ThumbnailState.Pending))
            {
                return listing.Items.ToDictionary(i => i.Id);
            }

            Assert.True(DateTime.UtcNow < deadline, "Thumbnails were still pending after 30 s.");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}
