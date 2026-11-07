using System.Net;
using System.Net.Http.Json;
using Dokument.Domain.Audit;
using Dokument.Domain.Files;
using Dokument.Domain.Filing;
using Dokument.Domain.Jobs;
using Dokument.Infrastructure.Persistence;
using Dokument.Server.Admin;
using Dokument.Server.Files;
using Dokument.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Dokument.IntegrationTests.FileTests;

namespace Dokument.IntegrationTests;

public sealed class FilingTests(PostgresFixture postgres)
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

    private static string Year =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.Local).Year.ToString();

    [Fact]
    public async Task A_tag_someone_adds_files_the_file_once_and_undo_sticks()
    {
        await using var app = await DokumentApp.StartAsync(postgres, FastWorker);
        using var alice = await SignInAsync(app, Alice);
        using var bob = await SignInAsync(app, Bob);
        var receipt = await UploadAsync(alice, "kvitto.pdf", TestFiles.TextPdf("Hej"), null);
        var inbox = await CreateFolderAsync(alice, null, "Inkorg");
        var other = await UploadAsync(alice, "kvitto.pdf", TestFiles.TextPdf("Annat"), inbox.Id);
        var scan = await UploadAsync(alice, "gammalt.pdf", TestFiles.TextPdf("Gammalt"), inbox.Id);
        await WaitForWorkerAsync(app);

        // A date taken people gave the file comes first, as written.
        var dated = await alice.PutAsJsonAsync(
            $"/api/v1/nodes/{scan}/details",
            new FileDetailsRequest(null, null, "1974"),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(HttpStatusCode.NoContent, dated.StatusCode);

        // Templates are checked, and a person has one rule per tag.
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await PostRuleAsync(alice, new FilingRuleRequest("receipt", "Kvitton/{day}"))
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await PostRuleAsync(alice, new FilingRuleRequest("receipt", " / "))).StatusCode
        );
        var rule = await CreateRuleAsync(
            alice,
            new FilingRuleRequest("receipt", " Kvitton / {year}/")
        );
        Assert.Equal("Kvitton/{year}", rule.Folder);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await PostRuleAsync(alice, new FilingRuleRequest("Receipt", "Annat"))).StatusCode
        );

        // Adding the tag moves the file; folders are made, and the second file is numbered.
        await TagAsync(alice, [receipt, other], "receipt");
        await WaitForWorkerAsync(app);
        var year = await FolderAsync(app, alice, ["Kvitton", Year]);
        Assert.Equal(year, await ParentAsync(app, receipt));
        Assert.Equal(year, await ParentAsync(app, other));
        Assert.Equal(
            ["kvitto (1).pdf", "kvitto.pdf"],
            (await ListAsync(alice, year)).Items.Select(i => i.Name).Order()
        );
        await TagAsync(alice, [scan], "receipt");
        await WaitForWorkerAsync(app);
        Assert.Equal(
            await FolderAsync(app, alice, ["Kvitton", "1974"]),
            await ParentAsync(app, scan)
        );
        Assert.Equal(3, (await RulesAsync(alice)).Single().Filed);

        // The move is the system's, naming the rule.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DokumentDbContext>();
            var move = await db
                .AuditEvents.Where(e =>
                    e.Action == AuditActions.NodeMove && e.TargetNodeId == receipt
                )
                .SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(AuditActorKind.System, move.ActorKind);
            Assert.Equal(rule.Id.ToString(), (string?)move.Details!["rule"]);
        }

        // The message lists both, with where they went; Bob sees none of it.
        var recent = await RecentAsync(alice);
        Assert.Equal(
            ["Kvitton / 1974", $"Kvitton / {Year}", $"Kvitton / {Year}"],
            recent.Select(f => f.Folder).Order()
        );
        Assert.Empty(await RecentAsync(bob));
        Assert.Equal(
            HttpStatusCode.NotFound,
            (
                await bob.PostAsync(
                    $"/api/v1/filings/{other}/undo",
                    null,
                    TestContext.Current.CancellationToken
                )
            ).StatusCode
        );

        // Undo puts it back where it was, and nothing files it again.
        var undo = await alice.PostAsync(
            $"/api/v1/filings/{other}/undo",
            null,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(HttpStatusCode.NoContent, undo.StatusCode);
        Assert.Equal(inbox.Id, await ParentAsync(app, other));
        Assert.Equal(2, (await RecentAsync(alice)).Count);
        await TagAsync(alice, [other], "receipt 2");
        await TagAsync(alice, [other], remove: "receipt");
        await TagAsync(alice, [other], "receipt");
        await WaitForWorkerAsync(app);
        Assert.Equal(inbox.Id, await ParentAsync(app, other));

        // A file moved away by hand stays there too.
        var move2 = await alice.PostAsJsonAsync(
            $"/api/v1/nodes/{receipt}/move",
            new MoveRequest(null),
            Json.Options,
            TestContext.Current.CancellationToken
        );
        Assert.True(move2.IsSuccessStatusCode, await move2.Content.ReadAsStringAsync());
        await UploadAsync(alice, "kvitto.pdf", TestFiles.TextPdf("Ny version"), null, receipt);
        await WaitForWorkerAsync(app);
        Assert.Null(await ParentAsync(app, receipt));
    }

    [Fact]
    public async Task Keyword_rules_file_new_files_and_existing_ones_only_when_asked()
    {
        await using var app = await DokumentApp.StartAsync(postgres, FastWorker);
        using var admin = await SignInAsync(app, Admin);
        using var alice = await SignInAsync(app, Alice);
        using var bob = await SignInAsync(app, Bob);
        await CreateTagRuleAsync(admin, new TagRuleRequest("receipt", "kvitto"));
        await CreateTagRuleAsync(admin, new TagRuleRequest("invoice", "faktura"));
        var old = await UploadAsync(alice, "gammalt.pdf", TestFiles.TextPdf("Kvitto 1"), null);
        await WaitForWorkerAsync(app);

        // Rules are ordered, and the first that matches wins.
        var invoices = await CreateRuleAsync(
            alice,
            new FilingRuleRequest("invoice", "Räkningar/{year}/{month}")
        );
        var receipts = await CreateRuleAsync(
            alice,
            new FilingRuleRequest("receipt", "Kvitton/{year}")
        );
        await PutAsync(
            alice,
            "/api/v1/me/filing-rules/order",
            new FilingRuleOrderRequest([receipts.Id, invoices.Id])
        );
        Assert.Equal(["receipt", "invoice"], (await RulesAsync(alice)).Select(r => r.Tag));

        // A new upload is filed once its text is read. The file that had the tag before the rule stays.
        var both = await UploadAsync(
            alice,
            "ica.pdf",
            TestFiles.TextPdf("Kvitto och faktura"),
            null
        );
        var bill = await UploadAsync(
            alice,
            "telia.pdf",
            TestFiles.TextPdf("Faktura från Telia"),
            null
        );
        var bobs = await UploadAsync(bob, "bob.pdf", TestFiles.TextPdf("Kvitto"), null);
        await WaitForWorkerAsync(app);
        Assert.Equal(
            await FolderAsync(app, alice, ["Kvitton", Year]),
            await ParentAsync(app, both)
        );
        var month = TimeZoneInfo
            .ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.Local)
            .Month.ToString("00");
        Assert.Equal(
            await FolderAsync(app, alice, ["Räkningar", Year, month]),
            await ParentAsync(app, bill)
        );
        Assert.Null(await ParentAsync(app, old));
        Assert.Null(await ParentAsync(app, bobs));

        // The preview counts the files that have the tag and aren't filed; asking files them.
        var preview = await PostAsync<FilingPreviewDto>(
            alice,
            "/api/v1/me/filing-rules/preview",
            new FilingPreviewRequest("receipt", "Kvitton/{year}")
        );
        Assert.Equal(1, preview.Files);
        Assert.Equal(
            ("gammalt.pdf", $"Kvitton / {Year}"),
            (preview.Examples.Single().Name, preview.Examples.Single().Folder)
        );
        await PutAsync(
            alice,
            $"/api/v1/me/filing-rules/{receipts.Id}",
            new FilingRuleRequest("receipt", "Kvitton/{year}", FileExisting: true)
        );
        await WaitForWorkerAsync(app);
        Assert.Equal(await FolderAsync(app, alice, ["Kvitton", Year]), await ParentAsync(app, old));

        // A rule that's off files nothing; deleting one leaves its files where they are.
        await PutAsync(
            alice,
            $"/api/v1/me/filing-rules/{invoices.Id}",
            new FilingRuleRequest("invoice", "Räkningar", Enabled: false)
        );
        var later = await UploadAsync(alice, "el.pdf", TestFiles.TextPdf("Faktura el"), null);
        await WaitForWorkerAsync(app);
        Assert.Null(await ParentAsync(app, later));
        var delete = await alice.DeleteAsync(
            $"/api/v1/me/filing-rules/{receipts.Id}",
            TestContext.Current.CancellationToken
        );
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(await FolderAsync(app, alice, ["Kvitton", Year]), await ParentAsync(app, old));
        Assert.Single(await RulesAsync(alice));
        Assert.Empty(await RulesAsync(bob));
    }

    [Fact]
    public async Task Admins_see_filing_jobs_on_the_jobs_page()
    {
        await using var app = await DokumentApp.StartAsync(postgres, FastWorker);
        using var admin = await SignInAsync(app, Admin);
        using var alice = await SignInAsync(app, Alice);
        var receipt = await UploadAsync(alice, "kvitto.pdf", TestFiles.TextPdf("Hej"), null);
        var other = await UploadAsync(alice, "annat.pdf", TestFiles.TextPdf("Hå"), null);
        await TagAsync(alice, [other], "receipt");
        await WaitForWorkerAsync(app);
        foreach (var queue in new[] { "file", "filingRun" })
        {
            await PutAsync(
                admin,
                $"/api/v1/admin/jobs/queues/{queue}/paused",
                new PauseQueueRequest(true)
            );
        }

        await CreateRuleAsync(
            alice,
            new FilingRuleRequest("receipt", "Kvitton/{year}", FileExisting: true)
        );
        await TagAsync(alice, [receipt], "receipt");

        var waiting = await admin.GetFromJsonAsync<JobList>(
            "/api/v1/admin/jobs?status=queued",
            Json.Options,
            TestContext.Current.CancellationToken
        );
        var file = Assert.Single(waiting!.Items, j => j.Type == JobKind.File);
        Assert.Equal(receipt, Assert.Single(file.Files).NodeId);
        var run = Assert.Single(waiting.Items, j => j.Type == JobKind.FilingRun);
        Assert.Equal("receipt", run.RuleTag);
        Assert.Empty(run.Files);
        Assert.Contains(
            (
                await admin.GetFromJsonAsync<List<JobQueueDto>>(
                    "/api/v1/admin/jobs/queues",
                    Json.Options,
                    TestContext.Current.CancellationToken
                )
            )!,
            q => q.Type == JobKind.FilingRun && q.Paused
        );
    }

    [Fact]
    public async Task A_rule_leaves_a_file_where_people_it_is_shared_with_see_it()
    {
        await using var app = await DokumentApp.StartAsync(postgres, FastWorker);
        using var alice = await SignInAsync(app, Alice);
        using var bob = await SignInAsync(app, Bob);
        var family = await CreateFolderAsync(alice, null, "Familjen");
        var bobId = (await DokumentApp.GetSessionAsync(bob)).User.Id;
        (
            await alice.PostAsJsonAsync(
                $"/api/v1/nodes/{family.Id}/shares",
                new ShareRequest(bobId, Dokument.Domain.Sharing.ShareRole.Editor),
                TestContext.Current.CancellationToken
            )
        ).EnsureSuccessStatusCode();

        // Bob adds a receipt to Alice's shared folder; her rule would move it where he can't see it.
        var shared = await UploadAsync(bob, "kvitto.pdf", TestFiles.TextPdf("Hej"), family.Id);
        var own = await UploadAsync(alice, "eget.pdf", TestFiles.TextPdf("Eget"), null);
        await CreateRuleAsync(alice, new FilingRuleRequest("receipt", "Kvitton"));
        await TagAsync(bob, [shared], "receipt");
        await TagAsync(alice, [own], "receipt");
        await WaitForWorkerAsync(app);

        Assert.Equal(family.Id, await ParentAsync(app, shared));
        Assert.Equal(await FolderAsync(app, alice, ["Kvitton"]), await ParentAsync(app, own));
        var rule = Assert.Single(await RulesAsync(alice));
        Assert.Equal(1, rule.Filed);
        Assert.Equal(1, rule.Skipped);
        Assert.Equal(["eget.pdf"], (await RecentAsync(alice)).Select(f => f.Name));
    }

    private static Task<HttpResponseMessage> PostRuleAsync(
        HttpClient client,
        FilingRuleRequest request
    ) =>
        client.PostAsJsonAsync(
            "/api/v1/me/filing-rules",
            request,
            Json.Options,
            TestContext.Current.CancellationToken
        );

    [Fact]
    public async Task A_run_files_what_people_have_and_refiles_after_a_rule_changes()
    {
        await using var app = await DokumentApp.StartAsync(postgres, FastWorker);
        using var alice = await SignInAsync(app, Alice);
        using var bob = await SignInAsync(app, Bob);
        var inbox = await CreateFolderAsync(alice, null, "Inkorg");
        var mine = await CreateFolderAsync(alice, null, "Eget");
        var before = await UploadAsync(alice, "före.pdf", TestFiles.TextPdf("Före"), inbox.Id);
        await TagAsync(alice, [before], "receipt");
        var rule = await CreateRuleAsync(alice, new FilingRuleRequest("receipt", "Kvitton"));
        var filed = await UploadAsync(alice, "filad.pdf", TestFiles.TextPdf("Filad"), null);
        var placed = await UploadAsync(alice, "flyttad.pdf", TestFiles.TextPdf("Flyttad"), null);
        await TagAsync(alice, [filed, placed], "receipt");
        await WaitForWorkerAsync(app);
        var kvitton = await FolderAsync(app, alice, ["Kvitton"]);
        Assert.Equal(kvitton, await ParentAsync(app, filed));
        Assert.Equal(inbox.Id, await ParentAsync(app, before));
        await PostAsync<NodeDto>(alice, $"/api/v1/nodes/{placed}/move", new MoveRequest(mine.Id));

        // The rule's folder changes; nothing moves until the person runs it.
        await PutAsync(
            alice,
            $"/api/v1/me/filing-rules/{rule.Id}",
            new FilingRuleRequest("receipt", "Kvitton/{year}")
        );
        var preview = await PostAsync<RunPreviewDto>(
            alice,
            "/api/v1/me/filing-runs/preview",
            new FilingRunRequest()
        );
        Assert.Equal(2, preview.Move);
        var group = Assert.Single(preview.Groups);
        Assert.Equal(($"Kvitton / {Year}", true, 2), (group.Folder, group.NewFolder, group.Count));
        Assert.Equal(
            [("filad.pdf", "Kvitton"), ("före.pdf", "Inkorg")],
            group.Files.Select(f => (f.Name, f.From)).OrderBy(f => f.Name)
        );
        Assert.Equal(1, preview.Placed);
        Assert.Equal(
            3,
            (
                await PostAsync<RunPreviewDto>(
                    alice,
                    "/api/v1/me/filing-runs/preview",
                    new FilingRunRequest(IncludePlaced: true)
                )
            ).Move
        );

        // Running it moves the two and leaves the one the person placed; the per-file message stays quiet.
        var run = await PostAsync<FilingRunDto>(
            alice,
            "/api/v1/me/filing-runs",
            new FilingRunRequest()
        );
        await WaitForWorkerAsync(app);
        var year = await FolderAsync(app, alice, ["Kvitton", Year]);
        Assert.Equal(year, await ParentAsync(app, filed));
        Assert.Equal(year, await ParentAsync(app, before));
        Assert.Equal(mine.Id, await ParentAsync(app, placed));
        var done = (
            await alice.GetFromJsonAsync<FilingRunDto>(
                "/api/v1/me/filing-runs/latest",
                Json.Options,
                TestContext.Current.CancellationToken
            )
        )!;
        Assert.Equal(
            (run.Id, FilingRunStatus.Done, 2, 2, 1, $"Kvitton / {Year}", true),
            (done.Id, done.Status, done.Total, done.Moved, done.Folders, done.Folder, done.CanUndo)
        );
        Assert.Empty(await RecentAsync(alice));

        // Undo puts each back where it was before the run.
        var undone = await PostAsync<UndoRunResult>(
            alice,
            $"/api/v1/me/filing-runs/{run.Id}/undo",
            new { }
        );
        Assert.Equal(2, undone.Files);
        Assert.Equal(kvitton, await ParentAsync(app, filed));
        Assert.Equal(inbox.Id, await ParentAsync(app, before));

        // A run on a selection only looks at it, and a folder brings what is in it. Others' ids are refused.
        var selection = await PostAsync<RunPreviewDto>(
            alice,
            "/api/v1/me/filing-runs/preview",
            new FilingRunRequest(NodeIds: [inbox.Id], IncludePlaced: true)
        );
        Assert.Equal(["före.pdf"], selection.Groups.SelectMany(g => g.Files).Select(f => f.Name));
        await CreateRuleAsync(bob, new FilingRuleRequest("receipt", "Kvitton"));
        var foreign = await bob.PostAsJsonAsync(
            "/api/v1/me/filing-runs/preview",
            new FilingRunRequest(NodeIds: [inbox.Id]),
            Json.Options,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    private static Task<FilingRuleDto> CreateRuleAsync(
        HttpClient client,
        FilingRuleRequest request
    ) => PostAsync<FilingRuleDto>(client, "/api/v1/me/filing-rules", request);

    private static async Task<List<FilingRuleDto>> RulesAsync(HttpClient client) =>
        (
            await client.GetFromJsonAsync<FilingRuleListing>(
                "/api/v1/me/filing-rules",
                Json.Options,
                TestContext.Current.CancellationToken
            )
        )!.Rules;

    private static async Task<List<FiledFileDto>> RecentAsync(HttpClient client) =>
        (
            await client.GetFromJsonAsync<FiledFiles>(
                "/api/v1/filings/recent",
                Json.Options,
                TestContext.Current.CancellationToken
            )
        )!.Items;

    private static Task CreateTagRuleAsync(HttpClient admin, TagRuleRequest request) =>
        PostAsync<TagRuleDto>(admin, "/api/v1/admin/tag-rules", request);

    private static async Task TagAsync(
        HttpClient client,
        List<Guid> ids,
        string? add = null,
        string? remove = null
    )
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/nodes/tags",
            new ChangeTagsRequest(ids, add is null ? [] : [add], remove is null ? [] : [remove]),
            Json.Options,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<Guid?> ParentAsync(DokumentApp app, Guid id)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DokumentDbContext>();
        return await db
            .Nodes.Where(n => n.Id == id)
            .Select(n => n.ParentId)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The folder at these names from the top of the person's files.</summary>
    private static async Task<Guid> FolderAsync(DokumentApp app, HttpClient client, string[] names)
    {
        Guid? folder = null;
        foreach (var name in names)
        {
            folder = (await ListAsync(client, folder))
                .Items.Single(i => i.Kind == NodeKind.Folder && i.Name == name)
                .Id;
        }

        return folder!.Value;
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(
            url,
            body,
            Json.Options,
            TestContext.Current.CancellationToken
        );
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (
            await response.Content.ReadFromJsonAsync<T>(
                Json.Options,
                TestContext.Current.CancellationToken
            )
        )!;
    }

    private static async Task PutAsync(HttpClient client, string url, object body)
    {
        var response = await client.PutAsJsonAsync(
            url,
            body,
            Json.Options,
            TestContext.Current.CancellationToken
        );
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

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

        await using var last = app.Services.CreateAsyncScope();
        var left = await last
            .ServiceProvider.GetRequiredService<DokumentDbContext>()
            .Jobs.Where(j => j.Status != JobStatus.Done)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Fail(
            "The worker didn't finish in time: "
                + string.Join(
                    ", ",
                    left.Select(j => $"{j.Type} {j.Subject} {j.Status} {j.RunAfter:O}")
                )
        );
    }
}
