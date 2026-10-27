using System.Text.Json.Nodes;
using Dokument.Application.Files;
using Dokument.Domain.Audit;
using Dokument.Domain.Files;
using Dokument.Domain.Filing;
using Dokument.Domain.Jobs;
using Dokument.Domain.Tags;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Jobs;
using Dokument.Infrastructure.Live;
using Dokument.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dokument.Infrastructure.Filing;

/// <param name="Filed">Files the rule moved that weren't moved back with Undo.</param>
/// <param name="Running">The rule is filing the files that already had its tag.</param>
public sealed record FilingRuleSummary(FilingRule Rule, int Filed, bool Running);

/// <param name="Folder">Where it would go, as "Kvitton / 2026".</param>
public sealed record FilingExample(Guid NodeId, string Name, string? MimeType, string Folder);

/// <param name="Files">The person's files that have the tag now and that no rule has filed.</param>
public sealed record FilingPreview(int Files, IReadOnlyList<FilingExample> Examples);

/// <summary>A file a rule moved, for the "Filed … in …" message with Undo.</summary>
/// <param name="Folder">Where it went, as "Kvitton / 2026".</param>
public sealed record FiledFile(
    Guid NodeId,
    string Name,
    string? MimeType,
    Guid? FolderId,
    string Folder,
    string Tag,
    DateTimeOffset FiledAt
);

/// <summary>
/// A person's own filing rules (docs/design.md §13.6): listing, changing and ordering them, previewing one, the files
/// rules just moved, and undoing a move. Rules only ever touch their owner's files.
/// </summary>
public sealed class FilingService(
    DokumentDbContext db,
    DriveService drive,
    Filer filer,
    AuditLog audit,
    JobQueue jobs,
    LiveChanges live,
    TimeProvider time
)
{
    private const int ExampleCount = 2;

    /// <summary>How far back the "Filed" message looks when a client doesn't say.</summary>
    private static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(2);

    public async Task<List<FilingRuleSummary>> ListAsync(
        Actor actor,
        CancellationToken ct = default
    )
    {
        var rules = await db
            .FilingRules.Where(r => r.OwnerId == actor.UserId)
            .OrderBy(r => r.Position)
            .ToListAsync(ct);
        var ids = rules.Select(r => (Guid?)r.Id).ToList();
        var filed = await db
            .NodeFilings.Where(f => ids.Contains(f.RuleId) && f.UndoneAt == null)
            .GroupBy(f => f.RuleId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key!.Value, g => g.Count, ct);
        var subjects = rules.Select(r => r.Id.ToString()).ToList();
        var running = (
            await db
                .Jobs.Where(j =>
                    j.Type == JobType.FilingRule
                    && subjects.Contains(j.Subject)
                    && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running)
                )
                .Select(j => j.Subject)
                .ToListAsync(ct)
        ).ToHashSet();
        return rules
            .Select(r => new FilingRuleSummary(
                r,
                filed.GetValueOrDefault(r.Id),
                running.Contains(r.Id.ToString())
            ))
            .ToList();
    }

    /// <summary>
    /// Adds a rule at the end of the list. It files files that get its tag from now on, and, with
    /// <paramref name="fileExisting"/>, the files that have it already.
    /// </summary>
    public async Task<FilingRule> CreateAsync(
        Actor actor,
        string tag,
        string folder,
        bool enabled,
        bool fileExisting,
        CancellationToken ct = default
    )
    {
        var (name, template) = Validate(tag, folder);
        var count = await db.FilingRules.CountAsync(r => r.OwnerId == actor.UserId, ct);
        if (count >= FilingRule.MaxPerPerson)
        {
            throw new DriveException(
                DriveError.InvalidRequest,
                $"You can have at most {FilingRule.MaxPerPerson} filing rules."
            );
        }

        await EnsureFreeAsync(actor, name, null, ct);
        var now = time.GetUtcNow();
        var last = await db
            .FilingRules.Where(r => r.OwnerId == actor.UserId)
            .MaxAsync(r => (int?)r.Position, ct);
        var rule = new FilingRule
        {
            OwnerId = actor.UserId,
            Tag = name,
            Folder = template,
            Position = (last ?? -1) + 1,
            Enabled = enabled,
            ActiveSince = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.FilingRules.Add(rule);
        Audit(actor, "create", rule);
        if (enabled && fileExisting)
        {
            await jobs.EnqueueAsync(
                JobType.FilingRule,
                rule.Id.ToString(),
                JobPriority.Interactive,
                ct
            );
        }

        await SaveAsync(actor, ct);
        return rule;
    }

    /// <summary>
    /// Changes a rule. Giving it another tag, or turning it back on, counts from now, like a new rule: files that
    /// already had the tag stay where they are unless <paramref name="fileExisting"/>.
    /// </summary>
    public async Task<FilingRule> UpdateAsync(
        Actor actor,
        Guid id,
        string tag,
        string folder,
        bool enabled,
        bool fileExisting,
        CancellationToken ct = default
    )
    {
        var rule = await FindAsync(actor, id, ct);
        var (name, template) = Validate(tag, folder);
        await EnsureFreeAsync(actor, name, id, ct);
        var now = time.GetUtcNow();
        if (enabled && (!rule.Enabled || rule.TagKey != name.Key))
        {
            rule.ActiveSince = now;
        }

        if (!rule.Tag.Equals(name) || !rule.Folder.Equals(template) || rule.Enabled != enabled)
        {
            rule.Tag = name;
            rule.Folder = template;
            rule.Enabled = enabled;
            rule.UpdatedAt = now;
            Audit(actor, "update", rule);
        }

        if (enabled && fileExisting)
        {
            await jobs.EnqueueAsync(
                JobType.FilingRule,
                rule.Id.ToString(),
                JobPriority.Interactive,
                ct
            );
        }

        await SaveAsync(actor, ct);
        return rule;
    }

    /// <summary>Deletes a rule. Files it moved stay where they are, and still won't be filed again.</summary>
    public async Task DeleteAsync(Actor actor, Guid id, CancellationToken ct = default)
    {
        var rule = await FindAsync(actor, id, ct);
        db.FilingRules.Remove(rule);
        Audit(actor, "delete", rule);
        await SaveAsync(actor, ct);
    }

    /// <summary>Puts the person's rules in this order. <paramref name="ids"/> must be all of them.</summary>
    public async Task ReorderAsync(
        Actor actor,
        IReadOnlyList<Guid> ids,
        CancellationToken ct = default
    )
    {
        var rules = await db.FilingRules.Where(r => r.OwnerId == actor.UserId).ToListAsync(ct);
        if (ids.Count != rules.Count || !ids.ToHashSet().SetEquals(rules.Select(r => r.Id)))
        {
            throw new DriveException(
                DriveError.InvalidRequest,
                "Give every one of your filing rules, once each."
            );
        }

        for (var i = 0; i < ids.Count; i++)
        {
            rules.Single(r => r.Id == ids[i]).Position = i;
        }

        audit.Add(
            actor,
            AuditActions.FilingRuleChange,
            details: new JsonObject
            {
                ["change"] = "reorder",
                ["rules"] = new JsonArray([.. ids.Select(i => (JsonNode)i.ToString())]),
            }
        );
        await SaveAsync(actor, ct);
    }

    /// <summary>What "Also file the files that have this tag now" would move, without changing anything.</summary>
    public async Task<FilingPreview> PreviewAsync(
        Actor actor,
        string? tag,
        string? folder,
        CancellationToken ct = default
    )
    {
        if (!TagName.TryCreate(tag, out var name, out _))
        {
            return new FilingPreview(0, []);
        }

        var files = Unfiled(actor, name.Key);
        var count = await files.CountAsync(ct);
        var examples = new List<FilingExample>();
        if (FolderTemplate.TryCreate(folder, out var template, out _))
        {
            foreach (
                var node in await files
                    .Include(n => n.CurrentVersion)
                    .OrderByDescending(n => n.ModifiedAt)
                    .Take(ExampleCount)
                    .ToListAsync(ct)
            )
            {
                var names = template.Resolve(await filer.DateOfAsync(node.CurrentVersion!, ct));
                examples.Add(
                    new FilingExample(
                        node.Id,
                        node.Name.Value,
                        node.CurrentVersion!.MimeType,
                        string.Join(" / ", names.Select(n => n.Value))
                    )
                );
            }
        }

        return new FilingPreview(count, examples);
    }

    /// <summary>Files everything of the rule's owner that has its tag now and that no rule has filed. Run by the worker.</summary>
    public async Task FileExistingAsync(Guid ruleId, CancellationToken ct = default)
    {
        var rule = await db
            .FilingRules.AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == ruleId && r.Enabled, ct);
        if (rule is null)
        {
            return;
        }

        var ids = await Unfiled(new Actor(rule.OwnerId), rule.TagKey)
            .OrderBy(n => n.CreatedAt)
            .Select(n => n.Id)
            .ToListAsync(ct);
        foreach (var id in ids)
        {
            await filer.FileAsync(id, ruleId, ct);
        }
    }

    /// <summary>
    /// Files the person's rules moved after <paramref name="after"/> (by default, the last two minutes) and that are
    /// still where the rule put them, oldest first.
    /// </summary>
    public async Task<List<FiledFile>> RecentAsync(
        Actor actor,
        DateTimeOffset? after,
        CancellationToken ct = default
    )
    {
        var since = after ?? time.GetUtcNow() - RecentWindow;
        var rows = await db
            .NodeFilings.Where(f => f.FiledAt > since && f.UndoneAt == null)
            .Join(
                db.Nodes.Include(n => n.CurrentVersion),
                f => f.NodeId,
                n => n.Id,
                (f, n) => new { Filing = f, Node = n }
            )
            .Where(r =>
                r.Node.OwnerId == actor.UserId
                && r.Node.TrashedAt == null
                && r.Node.ParentId == r.Filing.ToFolderId
            )
            .OrderBy(r => r.Filing.FiledAt)
            .Take(50)
            .ToListAsync(ct);
        var folders = await FolderNamesAsync(
            rows.Select(r => r.Filing.ToFolderId).OfType<Guid>().Distinct().ToList(),
            ct
        );
        return rows.Select(r => new FiledFile(
                r.Node.Id,
                r.Node.Name.Value,
                r.Node.CurrentVersion?.MimeType,
                r.Filing.ToFolderId,
                r.Filing.ToFolderId is { } id ? folders.GetValueOrDefault(id, "") : "",
                r.Filing.Tag,
                r.Filing.FiledAt
            ))
            .ToList();
    }

    /// <summary>
    /// Moves a filed file back where it was, or to the top of My files if that folder is gone. It stays there: no rule
    /// files it again.
    /// </summary>
    public async Task<Node> UndoAsync(Actor actor, Guid nodeId, CancellationToken ct = default)
    {
        var node = await drive.GetFileAsync(actor, nodeId, ct);
        var filing =
            node.OwnerId == actor.UserId
                ? await db.NodeFilings.SingleOrDefaultAsync(f => f.NodeId == nodeId, ct)
                : null;
        if (filing is null)
        {
            throw DriveException.NotFound("filing");
        }

        if (filing.UndoneAt is not null)
        {
            return node;
        }

        if (node.ParentId != filing.ToFolderId)
        {
            throw new DriveException(
                DriveError.InvalidRequest,
                "The file has been moved since it was filed."
            );
        }

        var back =
            filing.FromFolderId is { } fromId
            && await db.Nodes.AnyAsync(
                n => n.Id == fromId && n.TrashedAt == null && n.OwnerId == node.OwnerId,
                ct
            )
                ? filing.FromFolderId
                : null;

        // Saved with the move.
        filing.UndoneAt = time.GetUtcNow();
        if (back == node.ParentId)
        {
            await db.SaveChangesAsync(ct);
            return node;
        }

        return await drive.MoveAsync(actor, nodeId, back, ct);
    }

    /// <summary>The person's live files with the tag that no rule has filed.</summary>
    private IQueryable<Node> Unfiled(Actor actor, string tagKey) =>
        db.Nodes.Where(n =>
            n.OwnerId == actor.UserId
            && n.Kind == NodeKind.File
            && n.TrashedAt == null
            && n.CurrentVersion != null
            && db.EffectiveTags.Any(t => t.NodeId == n.Id && t.TagKey == tagKey)
            && !db.NodeFilings.Any(f => f.NodeId == n.Id)
        );

    /// <summary>Each folder's path from the top of My files, as "Kvitton / 2026".</summary>
    private async Task<Dictionary<Guid, string>> FolderNamesAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken ct
    )
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var folders = await db.Nodes.AsNoTracking().Where(n => ids.Contains(n.Id)).ToListAsync(ct);
        var chains = await drive.AncestorsAsync(folders, ct);
        return folders.ToDictionary(
            f => f.Id,
            f => string.Join(" / ", chains[f.Id].Append(f).Select(n => n.Name.Value))
        );
    }

    private async Task<FilingRule> FindAsync(Actor actor, Guid id, CancellationToken ct) =>
        await db.FilingRules.SingleOrDefaultAsync(r => r.Id == id && r.OwnerId == actor.UserId, ct)
        ?? throw DriveException.NotFound("rule");

    private static (TagName, FolderTemplate) Validate(string tag, string folder)
    {
        if (!TagName.TryCreate(tag, out var name, out var error))
        {
            throw new DriveException(DriveError.InvalidRequest, error);
        }

        return FolderTemplate.TryCreate(folder, out var template, out error)
            ? (name, template)
            : throw new DriveException(DriveError.InvalidRequest, error);
    }

    private async Task EnsureFreeAsync(
        Actor actor,
        TagName name,
        Guid? except,
        CancellationToken ct
    )
    {
        if (
            await db.FilingRules.AnyAsync(
                r => r.OwnerId == actor.UserId && r.TagKey == name.Key && r.Id != except,
                ct
            )
        )
        {
            throw new DriveException(
                DriveError.NameTaken,
                $"You already have a filing rule for the tag \"{name.Value}\"."
            );
        }
    }

    private async Task SaveAsync(Actor actor, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        await live.NotifyAsync(actor.UserId, LiveTopics.Account, ct);
        await tx.CommitAsync(ct);
    }

    private void Audit(Actor actor, string change, FilingRule rule) =>
        audit.Add(
            actor,
            AuditActions.FilingRuleChange,
            details: new JsonObject
            {
                ["change"] = change,
                ["rule"] = rule.Id.ToString(),
                ["tag"] = rule.Tag.Value,
                ["folder"] = rule.Folder.Value,
                ["enabled"] = rule.Enabled,
            }
        );
}
