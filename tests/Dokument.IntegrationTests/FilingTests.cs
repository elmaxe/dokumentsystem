using System.Net;
using System.Net.Http.Json;
using Dokument.Domain.Audit;
using Dokument.Domain.Files;
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
        await WaitForWorkerAsync(app);

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
        Assert.Equal(2, (await RulesAsync(alice)).Single().Filed);

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
        Assert.Equal(2, recent.Count);
        Assert.All(recent, f => Assert.Equal($"Kvitton / {Year}", f.Folder));
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
        Assert.Single(await RecentAsync(alice));
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
