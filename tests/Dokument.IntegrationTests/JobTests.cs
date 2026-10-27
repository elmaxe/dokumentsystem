using System.Net;
using System.Net.Http.Json;
using Dokument.Domain.Jobs;
using Dokument.Infrastructure.Jobs;
using Dokument.Infrastructure.Persistence;
using Dokument.Server.Admin;
using Dokument.Server.Files;
using Dokument.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Dokument.IntegrationTests.FileTests;

namespace Dokument.IntegrationTests;

public sealed class JobTests(PostgresFixture postgres)
{
    private static readonly FakeUser Admin = new(
        "admin-sub",
        "Anna Admin",
        "anna@example.com",
        "dokument-admins"
    );
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
    public async Task Paused_queues_hold_their_jobs_and_everyone_sees_where_a_file_is()
    {
        await using var app = await DokumentApp.StartAsync(postgres, FastWorker);
        using var admin = await SignInAsync(app, Admin);
        using var alice = await SignInAsync(app, Alice);
        foreach (var type in Enum.GetValues<JobKind>())
        {
            var paused = await admin.PutAsJsonAsync(
                $"/api/v1/admin/jobs/queues/{type.ToString().ToLowerInvariant()}/paused",
                new PauseQueueRequest(true)
            );
            Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
        }

        var photo = await UploadAsync(alice, "kvitto.png", TestFiles.Png(400, 300), null);
        var folder = await CreateFolderAsync(alice, null, "Kvitton");
        var inFolder = await UploadAsync(alice, "skanning.png", TestFiles.Png(300, 400), folder.Id);

        // The file list shows the badge: every step is still waiting.
        var listed = (await ListAsync(alice, null)).Items.Single(i => i.Id == photo);
        Assert.Equal(
            [JobKind.Thumbnail, JobKind.Metadata, JobKind.Text],
            listed.Processing.Select(s => s.Step)
        );
        Assert.All(listed.Processing, s => Assert.False(s.Running));
        Assert.Empty(
            (await ListAsync(alice, null)).Items.Single(i => i.Id == folder.Id).Processing
        );

        // The details panel: paused, with the place in the queue; no job ids or errors for non-admins.
        var steps = await ProcessingAsync(alice, inFolder);
        Assert.All(steps, s => Assert.Equal(StepState.Paused, s.State));
        Assert.All(steps, s => Assert.Equal(1, s.Ahead));
        Assert.All(steps, s => Assert.Null(s.JobId));

        // The admin page: counts per queue, and the files by name, whoever owns them.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await alice.GetAsync("/api/v1/admin/jobs/queues")).StatusCode
        );
        var queues = await QueuesAsync(admin);
        Assert.All(queues, q => Assert.True(q.Paused));
        Assert.All(queues, q => Assert.Equal(2, q.Waiting));
        var waiting = await JobsAsync(admin, "status=queued&type=text");
        Assert.False(waiting.More);
        Assert.Equal(
            ["kvitto.png", "skanning.png"],
            waiting.Items.Select(j => j.Files.Single().Name)
        );
        var file = waiting.Items[1].Files.Single();
        Assert.Equal("Alice Andersson", file.OwnerName);
        Assert.Equal(JobFileUse.Current, file.Use);
        Assert.Equal(["Kvitton"], file.Path.Select(p => p.Name));
        Assert.Equal("image/png", waiting.Items[1].MimeType);
        var page = await JobsAsync(admin, "status=queued&limit=2");
        Assert.True(page.More);

        // Unpausing lets the worker through, and the badge goes once it's done.
        foreach (var type in Enum.GetValues<JobKind>())
        {
            await admin.PutAsJsonAsync(
                $"/api/v1/admin/jobs/queues/{type}/paused",
                new PauseQueueRequest(false)
            );
        }

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while ((await ListAsync(alice, null)).Items.Any(i => i.Processing.Count > 0))
        {
            Assert.True(DateTime.UtcNow < deadline, "Jobs were still running after 60 s.");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        steps = await ProcessingAsync(alice, photo);
        Assert.Equal(StepState.Done, steps[0].State);
        Assert.All(steps, s => Assert.NotNull(s.At));
        Assert.All(
            steps,
            s =>
                Assert.DoesNotContain(
                    s.State,
                    new[] { StepState.Waiting, StepState.Running, StepState.Paused }
                )
        );
        Assert.Contains(await QueuesAsync(admin), q => q.DoneLastWeek == 2);
    }

    [Fact]
    public async Task Admins_see_why_a_job_failed_and_can_retry_or_clear_it()
    {
        await using var app = await DokumentApp.StartAsync(postgres, FastWorker);
        using var admin = await SignInAsync(app, Admin);
        using var alice = await SignInAsync(app, Alice);
        await admin.PutAsJsonAsync(
            "/api/v1/admin/jobs/queues/text/paused",
            new PauseQueueRequest(true)
        );
        var mine = await UploadAsync(admin, "mitt.png", TestFiles.Png(200, 200), null);
        var hers = await UploadAsync(alice, "hennes.png", TestFiles.Png(210, 200), null);
        await FailTextJobsAsync(app);

        var adminView = await ProcessingAsync(admin, mine);
        Assert.Equal(StepState.Failed, adminView[2].State);
        Assert.Equal("IOException: No space left on device", adminView[2].Error);
        Assert.NotNull(adminView[2].JobId);
        var aliceView = await ProcessingAsync(alice, hers);
        Assert.Equal(StepState.Failed, aliceView[2].State);
        Assert.Null(aliceView[2].Error);
        Assert.Null(aliceView[2].JobId);
        // A failed step isn't work in progress, so it doesn't keep the badge.
        Assert.DoesNotContain(
            (await ListAsync(alice, null)).Items.Single().Processing,
            s => s.Step == JobKind.Text
        );

        var failed = await JobsAsync(admin, "status=failed");
        Assert.Equal(2, failed.Items.Count);
        Assert.All(
            failed.Items,
            j => Assert.Equal("IOException: No space left on device", j.Error)
        );

        var retry = await admin.PostAsync($"/api/v1/admin/jobs/{adminView[2].JobId}/retry", null);
        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
        Assert.Equal(StepState.Paused, (await ProcessingAsync(admin, mine))[2].State);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (
                await admin.PostAsync($"/api/v1/admin/jobs/{adminView[2].JobId}/retry", null)
            ).StatusCode
        );

        var other = (await JobsAsync(admin, "status=failed")).Items.Single();
        Assert.Equal("hennes.png", other.Files.Single().Name);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await admin.DeleteAsync($"/api/v1/admin/jobs/{other.Id}")).StatusCode
        );
        Assert.Equal(StepState.NotStarted, (await ProcessingAsync(alice, hers))[2].State);

        await FailTextJobsAsync(app);
        var queues = await (
            await admin.PostAsync("/api/v1/admin/jobs/retry-failed?type=text", null)
        ).Content.ReadFromJsonAsync<List<JobQueueDto>>(Json.Options);
        var text = queues!.Single(q => q.Type == JobKind.Text);
        Assert.Equal((0, 1), (text.Failed, text.Waiting));
    }

    [Fact]
    public async Task A_job_shows_the_files_that_use_its_content()
    {
        await using var app = await DokumentApp.StartAsync(postgres, FastWorker);
        using var admin = await SignInAsync(app, Admin);
        using var alice = await SignInAsync(app, Alice);
        await admin.PutAsJsonAsync(
            "/api/v1/admin/jobs/queues/text/paused",
            new PauseQueueRequest(true)
        );
        var bytes = TestFiles.Png(240, 180);
        var first = await UploadAsync(alice, "strand.png", bytes, null);
        await UploadAsync(alice, "strand kopia.png", bytes, null, onConflict: null);
        await UploadAsync(alice, "strand.png", TestFiles.Png(250, 180), null, nodeId: first);

        var jobs = (await JobsAsync(admin, "status=queued&type=text")).Items;
        var original = jobs.Single(j => j.Files.Count == 2);
        Assert.Equal(
            [(JobFileUse.Current, "strand kopia.png"), (JobFileUse.OlderVersion, "strand.png")],
            original.Files.Select(f => (f.Use, f.Name))
        );
        Assert.Equal(1, original.Files[1].VersionNumber);
    }

    private static async Task FailTextJobsAsync(DokumentApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DokumentDbContext>();
        var jobs = await db
            .Jobs.Where(j => j.Type == JobType.Text && j.Status == JobStatus.Queued)
            .ToListAsync(TestContext.Current.CancellationToken);
        foreach (var job in jobs)
        {
            job.Fail(
                "IOException: No space left on device",
                RetryPolicy.Default with
                {
                    MaxAttempts = 0,
                },
                DateTimeOffset.UtcNow
            );
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<List<ProcessingStepDto>> ProcessingAsync(
        HttpClient client,
        Guid id
    ) =>
        (
            await client.GetFromJsonAsync<FileProcessing>(
                $"/api/v1/nodes/{id}/processing",
                Json.Options
            )
        )!.Steps;

    private static async Task<List<JobQueueDto>> QueuesAsync(HttpClient admin) =>
        (
            await admin.GetFromJsonAsync<List<JobQueueDto>>(
                "/api/v1/admin/jobs/queues",
                Json.Options
            )
        )!;

    private static async Task<JobList> JobsAsync(HttpClient admin, string query) =>
        (await admin.GetFromJsonAsync<JobList>($"/api/v1/admin/jobs?{query}", Json.Options))!;
}
