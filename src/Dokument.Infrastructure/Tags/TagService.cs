using System.Text.Json.Nodes;
using Dokument.Application.Files;
using Dokument.Domain.Audit;
using Dokument.Domain.Files;
using Dokument.Domain.Jobs;
using Dokument.Domain.Tags;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Filing;
using Dokument.Infrastructure.Live;
using Dokument.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dokument.Infrastructure.Tags;

/// <summary>A tag as a file shows it.</summary>
/// <param name="RuleId">The keyword rule that added it, for <see cref="TagSource.Rule"/>.</param>
public sealed record TagView(string Name, TagSource Source, Guid? RuleId);

/// <param name="Files">How many of the files the person can see have it.</param>
public sealed record TagCount(string Name, int Files);

/// <summary>
/// Tags on files (docs/design.md §13.4). Tags are shared: everyone who can see a file sees its tags, and anyone who can
/// change the file can add and remove them. Removing an automatic tag is remembered for that file, so rules don't put it
/// back. Suggestions and counts only ever come from files the person can see.
/// </summary>
public sealed class TagService(
    DokumentDbContext db,
    AuditLog audit,
    FilingQueue filing,
    LiveChanges live,
    TimeProvider time
)
{
    /// <summary>The most tags one request adds or removes.</summary>
    public const int MaxTagsPerRequest = 20;

    /// <summary>The most tags one file can have.</summary>
    public const int MaxTagsPerFile = 100;

    private IQueryable<Node> LiveFiles(Actor actor) =>
        db.Nodes.VisibleTo(actor).Where(n => n.TrashedAt == null && n.Kind == NodeKind.File);

    /// <summary>The tags each of these nodes shows, by name. Nodes without tags are left out.</summary>
    public async Task<Dictionary<Guid, List<TagView>>> ForNodesAsync(
        IReadOnlyCollection<Guid> nodeIds,
        CancellationToken ct = default
    )
    {
        if (nodeIds.Count == 0)
        {
            return [];
        }

        var ids = nodeIds.ToList();
        var tags = await db.EffectiveTags.Where(t => ids.Contains(t.NodeId)).ToListAsync(ct);
        return tags.GroupBy(t => t.NodeId)
            .ToDictionary(
                g => g.Key,
                g =>
                    g.OrderBy(t => t.TagKey, StringComparer.Ordinal)
                        .Select(t => new TagView(t.Tag, t.Source, t.RuleId))
                        .ToList()
            );
    }

    /// <summary>The automatic tags someone took off this file and that its bytes still have, so they can be put back.</summary>
    public async Task<List<string>> RemovedAsync(Node file, CancellationToken ct = default)
    {
        if (file.CurrentVersion is not { } version)
        {
            return [];
        }

        var hash = version.BlobHash;
        var tags = await db
            .NodeTagRemovals.Where(r =>
                r.NodeId == file.Id
                && db.BlobTags.Any(b => b.BlobHash == hash && b.TagKey == r.TagKey)
            )
            .OrderBy(r => r.TagKey)
            .Select(r => r.Tag)
            .ToListAsync(ct);
        return tags.Select(t => t.Value).ToList();
    }

    /// <summary>
    /// Adds and removes tags on these files. Adding a removed automatic tag puts it back; adding any other tag makes it
    /// the person's own, so it stays even if a rule stops matching. Removing takes off both.
    /// </summary>
    public async Task ChangeAsync(
        Actor actor,
        IReadOnlyCollection<Guid> ids,
        IReadOnlyCollection<string> add,
        IReadOnlyCollection<string> remove,
        CancellationToken ct = default
    )
    {
        var wanted = ids.Distinct().ToList();
        if (wanted.Count is 0 or > 1000)
        {
            throw new DriveException(DriveError.InvalidRequest, "Give 1 to 1000 ids.");
        }

        var adds = await CanonicalAsync(actor, Parse(add), ct);
        var removes = Parse(remove).Where(r => adds.All(a => a.Key != r.Key)).ToList();
        if (adds.Count + removes.Count == 0)
        {
            return;
        }

        var nodes = await db
            .Nodes.VisibleTo(actor)
            .Include(n => n.CurrentVersion)
            .Where(n => wanted.Contains(n.Id) && n.TrashedAt == null)
            .ToListAsync(ct);
        if (nodes.Count != wanted.Count)
        {
            throw DriveException.NotFound();
        }

        // Tags are shared, so changing them takes an editor (§13.4).
        var editable = await db.Nodes.EditableBy(actor).CountAsync(n => wanted.Contains(n.Id), ct);
        if (editable != wanted.Count)
        {
            throw DriveException.Forbidden(Access.Edit);
        }

        if (nodes.Any(n => n.IsFolder))
        {
            throw new DriveException(DriveError.InvalidRequest, "Only files can have tags.");
        }

        var keys = adds.Concat(removes).Select(t => t.Key).ToList();
        var userTags = await db
            .NodeTags.Where(t => wanted.Contains(t.NodeId) && keys.Contains(t.TagKey))
            .ToListAsync(ct);
        var removals = await db
            .NodeTagRemovals.Where(r => wanted.Contains(r.NodeId) && keys.Contains(r.TagKey))
            .ToListAsync(ct);
        var hashes = nodes.Select(n => n.CurrentVersion?.BlobHash).OfType<ContentHash>().ToList();
        var blobTags = (
            await db
                .BlobTags.Where(b => hashes.Contains(b.BlobHash) && keys.Contains(b.TagKey))
                .Select(b => new { b.BlobHash, b.TagKey })
                .ToListAsync(ct)
        )
            .Select(b => (b.BlobHash, b.TagKey))
            .ToHashSet();
        var counts =
            adds.Count == 0
                ? []
                : await db
                    .EffectiveTags.Where(t => wanted.Contains(t.NodeId))
                    .GroupBy(t => t.NodeId)
                    .Select(g => new { g.Key, Count = g.Count() })
                    .ToDictionaryAsync(g => g.Key, g => g.Count, ct);

        var now = time.GetUtcNow();
        var gained = new List<(Guid, Guid)>();
        foreach (var node in nodes)
        {
            bool Automatic(TagName tag) =>
                node.CurrentVersion is { } v && blobTags.Contains((v.BlobHash, tag.Key));
            NodeTag? Own(TagName tag) =>
                userTags.FirstOrDefault(t => t.NodeId == node.Id && t.TagKey == tag.Key);
            NodeTagRemoval? Removal(TagName tag) =>
                removals.FirstOrDefault(r => r.NodeId == node.Id && r.TagKey == tag.Key);
            bool Shown(TagName tag) =>
                Own(tag) is not null || (Automatic(tag) && Removal(tag) is null);

            var added = new List<string>();
            var removed = new List<string>();
            foreach (var tag in adds.Where(t => Own(t) is null))
            {
                var wasShown = Shown(tag);
                if (!wasShown && counts.GetValueOrDefault(node.Id) >= MaxTagsPerFile)
                {
                    throw new DriveException(
                        DriveError.InvalidRequest,
                        $"A file can have at most {MaxTagsPerFile} tags."
                    );
                }

                var removal = Removal(tag);
                if (removal is not null)
                {
                    db.NodeTagRemovals.Remove(removal);
                    removals.Remove(removal);
                }

                // Putting back a removed automatic tag only lifts the removal. Otherwise the tag becomes the
                // person's own, even where a rule also gives it, so it stays if the rule stops matching.
                if (removal is null || !Automatic(tag))
                {
                    var own = new NodeTag
                    {
                        NodeId = node.Id,
                        Tag = tag,
                        AddedBy = actor.UserId,
                        AddedAt = now,
                    };
                    db.NodeTags.Add(own);
                    userTags.Add(own);
                }

                if (!wasShown)
                {
                    counts[node.Id] = counts.GetValueOrDefault(node.Id) + 1;
                    added.Add(tag.Value);
                }
            }

            foreach (var tag in removes.Where(Shown))
            {
                if (Own(tag) is { } own)
                {
                    db.NodeTags.Remove(own);
                    userTags.Remove(own);
                }

                if (Automatic(tag) && Removal(tag) is null)
                {
                    var removal = new NodeTagRemoval
                    {
                        NodeId = node.Id,
                        Tag = tag,
                        RemovedBy = actor.UserId,
                        RemovedAt = now,
                    };
                    db.NodeTagRemovals.Add(removal);
                    removals.Add(removal);
                }

                removed.Add(tag.Value);
            }

            if (added.Count > 0)
            {
                gained.Add((node.OwnerId, node.Id));
            }

            if (added.Count + removed.Count > 0)
            {
                audit.Add(
                    actor,
                    AuditActions.FileTagsChange,
                    node.Id,
                    details: new JsonObject
                    {
                        ["added"] = new JsonArray([.. added.Select(a => (JsonNode)a)]),
                        ["removed"] = new JsonArray([.. removed.Select(r => (JsonNode)r)]),
                    }
                );
            }
        }

        // A tag someone adds can file the file (§13.6).
        await filing.RequestAsync(gained, JobPriority.Interactive, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        await live.NotifyAsync(nodes.Select(n => n.OwnerId), LiveTopics.Files, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Tags on files the person can see, most used first: those starting with <paramref name="q"/> (or with a word
    /// that does), or all of them when it's empty.
    /// </summary>
    public async Task<List<TagCount>> SuggestAsync(
        Actor actor,
        string? q,
        int limit,
        CancellationToken ct = default
    )
    {
        var files = LiveFiles(actor);
        var tags = db.EffectiveTags.Where(t => files.Any(n => n.Id == t.NodeId));
        var prefix = (q ?? "").Trim().ToLowerInvariant();
        if (prefix.Length > 0)
        {
            var escaped = prefix.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            tags = tags.Where(t =>
                EF.Functions.Like(t.TagKey, escaped + "%")
                || EF.Functions.Like(t.TagKey, "% " + escaped + "%")
            );
        }

        var counts = await tags.GroupBy(t => t.TagKey)
            .Select(g => new
            {
                Key = g.Key,
                Name = g.Min(t => t.Tag)!,
                Files = g.Count(),
            })
            .OrderByDescending(g => g.Files)
            .ThenBy(g => g.Key)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(ct);
        return counts.Select(c => new TagCount(c.Name, c.Files)).ToList();
    }

    private static List<TagName> Parse(IReadOnlyCollection<string> values)
    {
        if (values.Count > MaxTagsPerRequest)
        {
            throw new DriveException(
                DriveError.InvalidRequest,
                $"Give at most {MaxTagsPerRequest} tags at a time."
            );
        }

        var tags = new List<TagName>();
        foreach (var value in values)
        {
            if (!TagName.TryCreate(value, out var tag, out var error))
            {
                throw new DriveException(DriveError.InvalidRequest, error);
            }

            if (tags.All(t => t.Key != tag.Key))
            {
                tags.Add(tag);
            }
        }

        return tags;
    }

    /// <summary>
    /// The spelling a tag already has on files the person can see, or in a keyword rule, so "Receipt" joins "receipt"
    /// instead of showing two ways.
    /// </summary>
    private async Task<List<TagName>> CanonicalAsync(
        Actor actor,
        List<TagName> tags,
        CancellationToken ct
    )
    {
        if (tags.Count == 0)
        {
            return tags;
        }

        var keys = tags.Select(t => t.Key).ToList();
        var files = LiveFiles(actor);
        var known = await db
            .EffectiveTags.Where(t => keys.Contains(t.TagKey) && files.Any(n => n.Id == t.NodeId))
            .GroupBy(t => t.TagKey)
            .Select(g => new { g.Key, Name = g.Min(t => t.Tag)! })
            .ToDictionaryAsync(g => g.Key, g => g.Name, ct);
        foreach (
            var rule in await db
                .TagRules.Where(r => keys.Contains(r.TagKey))
                .Select(r => new { r.TagKey, r.Tag })
                .ToListAsync(ct)
        )
        {
            known.TryAdd(rule.TagKey, rule.Tag.Value);
        }

        return tags.Select(t => known.TryGetValue(t.Key, out var name) ? TagName.From(name) : t)
            .ToList();
    }
}
