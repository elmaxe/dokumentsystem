using System.Net;
using System.Net.Http.Json;
using Dokument.Domain.Jobs;
using Dokument.Domain.Tags;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Search;
using Dokument.Server.Admin;
using Dokument.Server.Files;
using Dokument.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Dokument.IntegrationTests.FileTests;

namespace Dokument.IntegrationTests;

public sealed class TagTests(PostgresFixture postgres)
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
    public async Task People_add_and_remove_tags_and_find_files_by_them()
    {
        await using var app = await DokumentApp.StartAsync(postgres, FastWorker);
        using var alice = await SignInAsync(app, Alice);
        using var bob = await SignInAsync(app, Bob);
        var notes = await UploadAsync(
            alice,
            "anteckningar.pdf",
            TestFiles.TextPdf("Deklaration, avdrag för resor"),
            null
        );
        var other = await UploadAsync(alice, "annat.pdf", TestFiles.TextPdf("Inget här"), null);
        var folder = await CreateFolderAsync(alice, null, "Mappar");
        await WaitForWorkerAsync(app);

        await ChangeAsync(alice, [notes, other], add: ["Tax 2026"]);
        await ChangeAsync(alice, [notes], add: ["tax 2026", "resor"]);
        var tags = await TagsAsync(alice, notes);
        Assert.Equal(["resor", "Tax 2026"], tags.Tags.Select(t => t.Name));
        Assert.All(tags.Tags, t => Assert.Equal(TagSource.User, t.Source));

        // Lists carry the tags, and a later spelling joins the first one.
        var listed = await alice.GetFromJsonAsync<FolderListing>(
            "/api/v1/nodes",
            Json.Options,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(
            ["Tax 2026"],
            listed!.Items.Single(i => i.Id == other).Tags.Select(t => t.Name)
        );

        // tag: is a filter; a plain word finds tags too, and mixes with words in the text.
        Assert.Equal(2, (await SearchAsync(alice, "tag:\"tax 2026\"")).Total);
        Assert.Equal(2, (await SearchAsync(alice, "", "&tag=TAX%202026")).Total);
        var byWord = await SearchAsync(alice, "tax");
        Assert.Equal(2, byWord.Total);
        Assert.All(byWord.Results, r => Assert.Equal(MatchSource.Tag, r.Source));
        var mixed = Assert.Single((await SearchAsync(alice, "tax avdrag")).Results);
        Assert.Equal(notes, mixed.Node.Id);

        var suggestions = await alice.GetFromJsonAsync<TagSuggestions>(
            "/api/v1/tags?q=ta",
            Json.Options,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(new TagSuggestion("Tax 2026", 2), Assert.Single(suggestions!.Tags));
        var suggest = await alice.GetFromJsonAsync<SuggestResponse>(
            "/api/v1/search/suggest?q=2026",
            Json.Options,
            TestContext.Current.CancellationToken
        );
        Assert.Equal("Tax 2026", Assert.Single(suggest!.Tags).Name);

        // Removing takes it off one file only.
        await ChangeAsync(alice, [other], remove: ["TAX 2026"]);
        Assert.Empty((await TagsAsync(alice, other)).Tags);
        Assert.Equal(1, (await SearchAsync(alice, "tag:\"tax 2026\"")).Total);

        // Others can't see or change them, folders can't have them, and names are checked.
        var bobs = (
            await bob.GetFromJsonAsync<TagSuggestions>(
                "/api/v1/tags",
                Json.Options,
                TestContext.Current.CancellationToken
            )
        )!;
        Assert.Empty(bobs.Tags);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await PostChangeAsync(bob, [notes], ["mine"], [])).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await PostChangeAsync(alice, [folder.Id], ["x"], [])).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await PostChangeAsync(alice, [notes], ["a, b"], [])).StatusCode
        );

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DokumentDbContext>();
        Assert.Equal(
            4,
            await db.AuditEvents.CountAsync(
                e => e.Action == "file.tags_change",
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public async Task Keyword_rules_tag_files_and_removed_tags_stay_removed()
    {
        await using var app = await DokumentApp.StartAsync(postgres, FastWorker);
        using var admin = await SignInAsync(app, Admin);
        using var alice = await SignInAsync(app, Alice);
        var before = await UploadAsync(
            alice,
            "ica.pdf",
            TestFiles.TextPdf("ICA Nära Hornstull\nKvittot nr 88213\nTotalt 200,35"),
            null
        );
        var unrelated = await UploadAsync(
            alice,
            "avtal.pdf",
            TestFiles.TextPdf("Hyresavtal för lägenheten"),
            null
        );
        await WaitForWorkerAsync(app);

        // Non-admins can't manage rules; the preview counts without changing anything.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (
                await alice.GetAsync(
                    "/api/v1/admin/tag-rules",
                    TestContext.Current.CancellationToken
                )
            ).StatusCode
        );
        var preview = await PostAsync<TagRulePreviewDto>(
            admin,
            "/api/v1/admin/tag-rules/preview",
            new TagRulePreviewRequest("receipt", "kvitto, \"tack för ditt köp\"")
        );
        Assert.Equal(1, preview.Files);
        var example = Assert.Single(preview.Matches);
        Assert.Equal("ica.pdf", example.Name);
        Assert.Contains("Kvittot", example.Snippet!.Text);
        Assert.Empty((await TagsAsync(alice, before)).Tags);

        // The preview lists every matching file, a page at a time.
        var next = await PostAsync<TagRulePreviewDto>(
            admin,
            "/api/v1/admin/tag-rules/preview",
            new TagRulePreviewRequest("receipt", "kvitto", Offset: 1)
        );
        Assert.Equal(1, next.Files);
        Assert.Empty(next.Matches);

        // Typos don't match: "kvitta" isn't "kvitto".
        Assert.Equal(
            0,
            (
                await PostAsync<TagRulePreviewDto>(
                    admin,
                    "/api/v1/admin/tag-rules/preview",
                    new TagRulePreviewRequest(null, "kvitta")
                )
            ).Files
        );

        // A new rule runs over the files already there.
        var rule = await PostAsync<TagRuleDto>(
            admin,
            "/api/v1/admin/tag-rules",
            new TagRuleRequest("receipt", "kvitto,   \"tack för ditt köp\" , kvitto")
        );
        Assert.Equal("kvitto, \"tack för ditt köp\"", rule.Words);
        await WaitForWorkerAsync(app);
        var tag = Assert.Single((await TagsAsync(alice, before)).Tags);
        Assert.Equal(("receipt", TagSource.Rule, rule.Id), (tag.Name, tag.Source, tag.RuleId));
        Assert.Empty((await TagsAsync(alice, unrelated)).Tags);
        Assert.Equal(1, (await ListRulesAsync(admin)).Single().Files);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await admin.PostAsJsonAsync(
                    "/api/v1/admin/tag-rules",
                    new TagRuleRequest("Receipt", "x"),
                    Json.Options,
                    TestContext.Current.CancellationToken
                )
            ).StatusCode
        );

        // New files get it after their text is read, and searching the tag's word finds Swedish receipts.
        var after = await UploadAsync(
            alice,
            "kvitto-2.pdf",
            TestFiles.TextPdf("Tack för ditt köp!"),
            null
        );
        await WaitForWorkerAsync(app);
        Assert.Equal("receipt", Assert.Single((await TagsAsync(alice, after)).Tags).Name);
        Assert.Equal(2, (await SearchAsync(alice, "receipt")).Total);

        // Removed from one file, it stays removed when the rule runs again, until it's put back.
        await ChangeAsync(alice, [before], remove: ["receipt"]);
        var removed = await TagsAsync(alice, before);
        Assert.Empty(removed.Tags);
        Assert.Equal(["receipt"], removed.Removed);
        await PutAsync(
            admin,
            $"/api/v1/admin/tag-rules/{rule.Id}",
            new TagRuleRequest("receipt", "kvitto, kvittot, \"tack för ditt köp\"")
        );
        await WaitForWorkerAsync(app);
        Assert.Empty((await TagsAsync(alice, before)).Tags);
        Assert.Equal(1, (await SearchAsync(alice, "tag:receipt")).Total);
        await ChangeAsync(alice, [before], add: ["receipt"]);
        var back = Assert.Single((await TagsAsync(alice, before)).Tags);
        Assert.Equal(TagSource.Rule, back.Source);

        // A tag someone also added stays when the rule is turned off; the rule's own go at once.
        await ChangeAsync(alice, [after], add: ["receipt"]);
        await PutAsync(
            admin,
            $"/api/v1/admin/tag-rules/{rule.Id}",
            new TagRuleRequest("receipt", "kvitto, kvittot, \"tack för ditt köp\"", Enabled: false)
        );
        Assert.Empty((await TagsAsync(alice, before)).Tags);
        Assert.Equal(TagSource.User, Assert.Single((await TagsAsync(alice, after)).Tags).Source);

        var deleted = await admin.DeleteAsync(
            $"/api/v1/admin/tag-rules/{rule.Id}",
            TestContext.Current.CancellationToken
        );
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty(await ListRulesAsync(admin));
    }

    private static async Task<FileTags> TagsAsync(HttpClient client, Guid id) =>
        (
            await client.GetFromJsonAsync<FileTags>(
                $"/api/v1/nodes/{id}/tags",
                Json.Options,
                TestContext.Current.CancellationToken
            )
        )!;

    private static Task<HttpResponseMessage> PostChangeAsync(
        HttpClient client,
        List<Guid> ids,
        List<string> add,
        List<string> remove
    ) =>
        client.PostAsJsonAsync(
            "/api/v1/nodes/tags",
            new ChangeTagsRequest(ids, add, remove),
            Json.Options,
            TestContext.Current.CancellationToken
        );

    private static async Task ChangeAsync(
        HttpClient client,
        List<Guid> ids,
        List<string>? add = null,
        List<string>? remove = null
    )
    {
        var response = await PostChangeAsync(client, ids, add ?? [], remove ?? []);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(
            url,
            body,
            Json.Options,
            TestContext.Current.CancellationToken
        );
        response.EnsureSuccessStatusCode();
        return (
            await response.Content.ReadFromJsonAsync<T>(
                Json.Options,
                TestContext.Current.CancellationToken
            )
        )!;
    }

    private static async Task PutAsync(HttpClient client, string url, object body) =>
        (
            await client.PutAsJsonAsync(
                url,
                body,
                Json.Options,
                TestContext.Current.CancellationToken
            )
        ).EnsureSuccessStatusCode();

    private static async Task<List<TagRuleDto>> ListRulesAsync(HttpClient client) =>
        (
            await client.GetFromJsonAsync<TagRuleListing>(
                "/api/v1/admin/tag-rules",
                Json.Options,
                TestContext.Current.CancellationToken
            )
        )!.Rules;

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
}
