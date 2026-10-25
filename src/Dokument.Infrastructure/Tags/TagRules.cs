using System.Text.Json.Nodes;
using Dokument.Application.Files;
using Dokument.Domain.Audit;
using Dokument.Domain.Files;
using Dokument.Domain.Jobs;
using Dokument.Domain.Search;
using Dokument.Domain.Tags;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Jobs;
using Dokument.Infrastructure.Live;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Search;
using Microsoft.EntityFrameworkCore;

namespace Dokument.Infrastructure.Tags;

/// <param name="Files">Live files that show the rule's tag now.</param>
/// <param name="Running">The rule is being run over every file after a change.</param>
public sealed record TagRuleSummary(TagRule Rule, int Files, bool Running);

/// <summary>A file a rule would tag, for the preview. Admins see every owner's files, as on the Jobs page.</summary>
public sealed record TagRuleExample(Guid NodeId, string Name, string? MimeType, Snippet? Snippet);

/// <param name="Files">Live files that would show the tag, leaving out those it was removed from.</param>
public sealed record TagRulePreview(int Files, IReadOnlyList<TagRuleExample> Examples);

/// <summary>
/// Keyword rules (docs/design.md §13.4): an admin's tag plus words, matched against the stored text and metadata of
/// every blob with the BM25 index, so a rule never reads a file again. The worker runs the rules on a blob when its
/// text or metadata stage finishes, and runs one rule over everything after an admin changes it.
/// </summary>
public sealed class TagRules(
    DokumentDbContext db,
    AuditLog audit,
    JobQueue jobs,
    LiveChanges live,
    TimeProvider time
)
{
    public const int MaxRules = 500;
    private const int ExampleCount = 3;

    public async Task<List<TagRuleSummary>> ListAsync(CancellationToken ct = default)
    {
        var rules = await db.TagRules.OrderBy(r => r.TagKey).ToListAsync(ct);
        var counts = await db
            .EffectiveTags.Where(t =>
                t.RuleId != null && db.Nodes.Any(n => n.Id == t.NodeId && n.TrashedAt == null)
            )
            .GroupBy(t => t.RuleId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key!.Value, g => g.Count, ct);
        var running = await RunningAsync(ct);
        return rules
            .Select(r => new TagRuleSummary(
                r,
                counts.GetValueOrDefault(r.Id),
                running.Contains(r.Id.ToString())
            ))
            .ToList();
    }

    public async Task<TagRule> CreateAsync(
        Actor actor,
        string tag,
        string words,
        bool enabled,
        CancellationToken ct = default
    )
    {
        var (name, parsed) = Validate(tag, words);
        if (await db.TagRules.CountAsync(ct) >= MaxRules)
        {
            throw new DriveException(
                DriveError.InvalidRequest,
                $"A server can have at most {MaxRules} rules."
            );
        }

        await EnsureFreeAsync(name, null, ct);
        var now = time.GetUtcNow();
        var rule = new TagRule
        {
            Tag = name,
            Words = parsed,
            Enabled = enabled,
            CreatedBy = actor.UserId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.TagRules.Add(rule);
        Audit(actor, "create", rule);
        if (enabled)
        {
            await jobs.EnqueueAsync(
                JobType.TagRule,
                rule.Id.ToString(),
                JobPriority.Background,
                ct
            );
        }

        await db.SaveChangesAsync(ct);
        return rule;
    }

    /// <summary>
    /// Changes a rule. A changed tag or words runs it over every file again in the background; turning it off takes
    /// its tags off at once.
    /// </summary>
    public async Task<TagRule> UpdateAsync(
        Actor actor,
        Guid id,
        string tag,
        string words,
        bool enabled,
        CancellationToken ct = default
    )
    {
        var rule = await FindAsync(id, ct);
        var (name, parsed) = Validate(tag, words);
        await EnsureFreeAsync(name, id, ct);
        var changed =
            !rule.Tag.Equals(name) || !rule.Words.Equals(parsed) || rule.Enabled != enabled;
        if (!changed)
        {
            return rule;
        }

        rule.Tag = name;
        rule.Words = parsed;
        rule.Enabled = enabled;
        rule.UpdatedAt = time.GetUtcNow();
        Audit(actor, "update", rule);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (enabled)
        {
            await jobs.EnqueueAsync(
                JobType.TagRule,
                rule.Id.ToString(),
                JobPriority.Background,
                ct
            );
        }
        else
        {
            await db.BlobTags.Where(b => b.RuleId == id).ExecuteDeleteAsync(ct);
            await live.NotifyEveryoneAsync(LiveTopics.Files, ct);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return rule;
    }

    public async Task DeleteAsync(Actor actor, Guid id, CancellationToken ct = default)
    {
        var rule = await FindAsync(id, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.BlobTags.Where(b => b.RuleId == id).ExecuteDeleteAsync(ct);
        db.TagRules.Remove(rule);
        Audit(actor, "delete", rule);
        await db.SaveChangesAsync(ct);
        await live.NotifyEveryoneAsync(LiveTopics.Files, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>What a rule would tag, without changing anything: a count and a few of the newest files.</summary>
    public async Task<TagRulePreview> PreviewAsync(
        string? tag,
        string words,
        CancellationToken ct = default
    )
    {
        if (!TagRuleWords.TryParse(words, out var parsed, out var error))
        {
            throw new DriveException(DriveError.InvalidRequest, error);
        }

        var key = TagName.TryCreate(tag, out var name, out _) ? name.Key : null;
        var matching = MatchingBlobs(parsed);
        var files = db
            .Nodes.Where(n =>
                n.TrashedAt == null
                && n.CurrentVersion != null
                && matching.Contains(n.CurrentVersion.BlobHash)
            )
            .Where(n =>
                key == null || !db.NodeTagRemovals.Any(r => r.NodeId == n.Id && r.TagKey == key)
            );
        var count = await files.CountAsync(ct);
        var examples = await files
            .Include(n => n.CurrentVersion)
            .OrderByDescending(n => n.ModifiedAt)
            .Take(ExampleCount)
            .ToListAsync(ct);
        var query = BmQuery.Rule(parsed);
        var result = new List<TagRuleExample>();
        foreach (var node in examples)
        {
            var hash = node.CurrentVersion!.BlobHash.ToArray();
            var text = await db
                .Database.SqlQuery<string>(
                    $"""SELECT text AS "Value" FROM content_pages WHERE id @@@ {query}::jsonb AND blob_hash = {hash} ORDER BY page_no LIMIT 1"""
                )
                .FirstOrDefaultAsync(ct);
            result.Add(
                new TagRuleExample(
                    node.Id,
                    node.Name.Value,
                    node.CurrentVersion.MimeType,
                    text is null ? null : Highlighter.Snip(text, parsed.AllWords, 120)
                )
            );
        }

        return new TagRulePreview(count, result);
    }

    /// <summary>Runs one rule over every blob's stored text: its old tags go, and every blob that matches now gets the tag.</summary>
    public async Task ApplyRuleAsync(Guid ruleId, CancellationToken ct = default)
    {
        var rule = await db.TagRules.AsNoTracking().SingleOrDefaultAsync(r => r.Id == ruleId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.BlobTags.Where(b => b.RuleId == ruleId).ExecuteDeleteAsync(ct);
        if (rule is { Enabled: true })
        {
            var now = time.GetUtcNow();
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO blob_tags (blob_hash, tag, tag_key, source, rule_id, created_at)
                SELECT DISTINCT blob_hash, {rule.Tag.Value}, {rule.TagKey}, 'rule', {rule.Id}, {now}
                FROM content_pages WHERE id @@@ {BmQuery.Rule(rule.Words)}::jsonb
                ON CONFLICT DO NOTHING
                """,
                ct
            );
        }

        await live.NotifyEveryoneAsync(LiveTopics.Files, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Runs every rule on one blob, after its text or metadata was read: it ends up with the rule tags that match.</summary>
    public async Task ApplyToBlobAsync(ContentHash hash, CancellationToken ct = default)
    {
        var rules = await db.TagRules.AsNoTracking().Where(r => r.Enabled).ToListAsync(ct);
        var bytes = hash.ToArray();
        var matched = new List<TagRule>();
        foreach (var rule in rules)
        {
            var hit = await db
                .Database.SqlQuery<int>(
                    $"""SELECT 1 AS "Value" FROM content_pages WHERE id @@@ {BmQuery.Rule(rule.Words)}::jsonb AND blob_hash = {bytes} LIMIT 1"""
                )
                .AnyAsync(ct);
            if (hit)
            {
                matched.Add(rule);
            }
        }

        var before = await db
            .BlobTags.Where(b => b.BlobHash == hash && b.Source == TagSource.Rule)
            .Select(b => b.RuleId)
            .ToListAsync(ct);
        if (before.ToHashSet().SetEquals(matched.Select(r => (Guid?)r.Id)))
        {
            return;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db
            .BlobTags.Where(b => b.BlobHash == hash && b.Source == TagSource.Rule)
            .ExecuteDeleteAsync(ct);
        var now = time.GetUtcNow();
        foreach (var rule in matched.DistinctBy(r => r.TagKey))
        {
            db.BlobTags.Add(
                new BlobTag
                {
                    BlobHash = hash,
                    Tag = rule.Tag,
                    Source = TagSource.Rule,
                    RuleId = rule.Id,
                    CreatedAt = now,
                }
            );
        }

        await db.SaveChangesAsync(ct);
        var owners = await db
            .Nodes.Where(n => n.CurrentVersion != null && n.CurrentVersion.BlobHash == hash)
            .Select(n => n.OwnerId)
            .Distinct()
            .ToListAsync(ct);
        await live.NotifyAsync(owners, LiveTopics.Files, ct);
        await tx.CommitAsync(ct);
    }

    private IQueryable<ContentHash> MatchingBlobs(TagRuleWords words) =>
        db
            .ContentPages.FromSql(
                $"SELECT * FROM content_pages WHERE id @@@ {BmQuery.Rule(words)}::jsonb"
            )
            .Select(p => p.BlobHash);

    private async Task<HashSet<string>> RunningAsync(CancellationToken ct) =>
        (
            await db
                .Jobs.Where(j =>
                    j.Type == JobType.TagRule
                    && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running)
                )
                .Select(j => j.Subject)
                .ToListAsync(ct)
        ).ToHashSet();

    private async Task<TagRule> FindAsync(Guid id, CancellationToken ct) =>
        await db.TagRules.SingleOrDefaultAsync(r => r.Id == id, ct)
        ?? throw DriveException.NotFound("rule");

    private static (TagName, TagRuleWords) Validate(string tag, string words)
    {
        if (!TagName.TryCreate(tag, out var name, out var error))
        {
            throw new DriveException(DriveError.InvalidRequest, error);
        }

        return TagRuleWords.TryParse(words, out var parsed, out error)
            ? (name, parsed)
            : throw new DriveException(DriveError.InvalidRequest, error);
    }

    private async Task EnsureFreeAsync(TagName name, Guid? except, CancellationToken ct)
    {
        if (await db.TagRules.AnyAsync(r => r.TagKey == name.Key && r.Id != except, ct))
        {
            throw new DriveException(
                DriveError.NameTaken,
                $"There's already a rule for the tag \"{name.Value}\"."
            );
        }
    }

    private void Audit(Actor actor, string change, TagRule rule) =>
        audit.Add(
            actor,
            AuditActions.TagRuleChange,
            details: new JsonObject
            {
                ["change"] = change,
                ["rule"] = rule.Id.ToString(),
                ["tag"] = rule.Tag.Value,
                ["words"] = rule.Words.Value,
                ["enabled"] = rule.Enabled,
            }
        );
}
