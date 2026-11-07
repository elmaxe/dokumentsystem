using System.Text.Json.Nodes;
using Dokument.Domain.Audit;
using Dokument.Domain.Files;
using Dokument.Domain.Filing;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Live;
using Dokument.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dokument.Infrastructure.Filing;

/// <summary>What a run of filing rules does with one file (docs/design.md §13.6).</summary>
public enum FilingOutcome
{
    /// <summary>It moves to its rule's folder.</summary>
    Move,

    /// <summary>It is in its rule's folder already.</summary>
    AlreadyThere,

    /// <summary>Its owner put it somewhere after it was filed, or pressed Undo, and the run leaves it there.</summary>
    Placed,

    /// <summary>The folder would hide it from people it is shared with, so it stays.</summary>
    Shared,

    /// <summary>A file has the name of one of the rule's folders, so it can't go there.</summary>
    Blocked,
}

/// <param name="FromFolderId">Where it is now, null for the top of My files.</param>
/// <param name="Folder">The rule's folder for this file, from the top of My files.</param>
/// <param name="NewFolder">Some of <paramref name="Folder"/> doesn't exist yet and would be made.</param>
public sealed record PlannedFiling(
    Guid NodeId,
    string Name,
    string? MimeType,
    Guid? FromFolderId,
    FilingRule Rule,
    IReadOnlyList<NodeName> Folder,
    bool NewFolder,
    FilingOutcome Outcome
);

/// <param name="Items">Each of the person's files that a rule of the run matches, in the order they'd be filed.</param>
/// <param name="NoRule">The person's files in scope that have no tag of the run's rules.</param>
/// <param name="NotYours">Files in scope that someone else owns; rules only move their owner's files.</param>
public sealed record FilingPlan(List<PlannedFiling> Items, int NoRule, int NotYours);

/// <summary>
/// Runs a person's filing rules on their files (docs/design.md §13.6). The first enabled rule whose tag the file has
/// moves it to the rule's folder, made if missing, with the file's year and month. By itself a rule files a file once,
/// for a tag it got while the rule was on; after that the file stays wherever its owner puts it. A run looks at the
/// files the person already has, whenever they got their tags. Moves are audited as the system's, naming the rule.
/// </summary>
public sealed partial class Filer(
    DokumentDbContext db,
    DriveService drive,
    AuditLog audit,
    LiveChanges live,
    TimeProvider time,
    ILogger<Filer> logger
)
{
    /// <summary>
    /// Files the file if one of its owner's rules matches a tag it got while the rule was on, and no rule has filed it
    /// yet. Returns whether it was filed.
    /// </summary>
    public async Task<bool> FileAsync(Guid nodeId, CancellationToken ct = default)
    {
        var node = await LiveFileAsync(nodeId, ct);
        if (node is null || await db.NodeFilings.AnyAsync(f => f.NodeId == nodeId, ct))
        {
            return false;
        }

        var rules = await RulesAsync(node.OwnerId, null, ct);
        var tags = await db
            .EffectiveTags.Where(t => t.NodeId == nodeId)
            .ToDictionaryAsync(t => t.TagKey, t => t.Since, ct);
        var rule = rules.FirstOrDefault(r =>
            tags.TryGetValue(r.TagKey, out var since) && since >= r.ActiveSince
        );
        return rule is not null
            && await ApplyAsync(node, rule, null, ct)
                is FilingOutcome.Move
                    or FilingOutcome.AlreadyThere;
    }

    /// <summary>
    /// What a run of <paramref name="ruleId"/> (or every rule that is on) over <paramref name="nodeIds"/> (files, and
    /// folders with everything in them, or all the owner's files) would do, without changing anything. The ids must
    /// be ones the person can see.
    /// </summary>
    public async Task<FilingPlan> PlanAsync(
        Guid ownerId,
        Guid? ruleId,
        IReadOnlyCollection<Guid>? nodeIds,
        bool includePlaced,
        CancellationToken ct = default
    )
    {
        var rules = await RulesAsync(ownerId, ruleId, ct);
        var files = db.Nodes.Where(n =>
            n.Kind == NodeKind.File && n.TrashedAt == null && n.CurrentVersionId != null
        );
        var notYours = 0;
        if (nodeIds is not null)
        {
            var scope = await ScopeAsync(nodeIds, ct);
            files = files.Where(n => scope.Contains(n.Id));
            notYours = await files.CountAsync(n => n.OwnerId != ownerId, ct);
        }

        var mine = files.Where(n => n.OwnerId == ownerId);
        var keys = rules.Select(r => r.TagKey).ToList();
        var tagged = await db
            .EffectiveTags.Where(t => keys.Contains(t.TagKey))
            .Join(mine, t => t.NodeId, n => n.Id, (t, n) => new { t.NodeId, t.TagKey })
            .ToListAsync(ct);
        var tagsOf = tagged
            .GroupBy(t => t.NodeId)
            .ToDictionary(g => g.Key, g => g.Select(t => t.TagKey).ToHashSet());
        var noRule = await mine.CountAsync(ct) - tagsOf.Count;

        var ids = tagsOf.Keys.ToList();
        var nodes = await db
            .Nodes.AsNoTracking()
            .Include(n => n.CurrentVersion)
            .Where(n => ids.Contains(n.Id))
            .OrderBy(n => n.CreatedAt)
            .ToListAsync(ct);
        var filings = await db
            .NodeFilings.AsNoTracking()
            .Where(f => ids.Contains(f.NodeId))
            .ToDictionaryAsync(f => f.NodeId, ct);
        var dates = await DatesAsync(nodes.Select(n => n.CurrentVersion!).ToList(), ct);
        var folders = new FolderLookup(this, ownerId);

        var items = new List<PlannedFiling>();
        foreach (var node in nodes)
        {
            var rule = rules.First(r => tagsOf[node.Id].Contains(r.TagKey));
            var names = rule.Folder.Resolve(dates[node.CurrentVersion!.Id]);
            var (folder, complete, blocked) = await folders.FindAsync(names, ct);
            var outcome =
                blocked ? FilingOutcome.Blocked
                : complete && folder!.Id == node.ParentId ? FilingOutcome.AlreadyThere
                : filings.GetValueOrDefault(node.Id)?.PlacedByOwner(node.ParentId) == true
                && !includePlaced
                    ? FilingOutcome.Placed
                : await folders.HidesAsync(node, folder, ct) ? FilingOutcome.Shared
                : FilingOutcome.Move;
            items.Add(
                new PlannedFiling(
                    node.Id,
                    node.Name.Value,
                    node.CurrentVersion.MimeType,
                    node.ParentId,
                    rule,
                    names,
                    !complete,
                    outcome
                )
            );
        }

        return new FilingPlan(items, noRule, notYours);
    }

    /// <summary>
    /// Files one file for a run with <paramref name="rule"/>, after checking again that the run should: a file that
    /// is gone, or that its owner placed since the run looked, stays. Returns what happened.
    /// </summary>
    public async Task<FilingOutcome?> FileForRunAsync(
        Guid nodeId,
        FilingRule rule,
        FilingRun run,
        CancellationToken ct = default
    )
    {
        var node = await LiveFileAsync(nodeId, ct);
        if (node is null || node.OwnerId != run.OwnerId)
        {
            return null;
        }

        var filing = await db.NodeFilings.SingleOrDefaultAsync(f => f.NodeId == nodeId, ct);
        if (filing?.PlacedByOwner(node.ParentId) == true && !run.IncludePlaced)
        {
            return FilingOutcome.Placed;
        }

        return await ApplyAsync(node, rule, run.Id, ct);
    }

    /// <summary>
    /// Moves the file to the rule's folder and records it, or records that it was skipped because the folder would hide
    /// it from people. A file already there is recorded as filed without a move.
    /// </summary>
    private async Task<FilingOutcome> ApplyAsync(
        Node node,
        FilingRule rule,
        Guid? runId,
        CancellationToken ct
    )
    {
        var names = rule.Folder.Resolve(await DateOfAsync(node.CurrentVersion!, ct));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var folder = await EnsureFoldersAsync(node.OwnerId, names, rule, ct);
        if (folder is null)
        {
            LogBlocked(logger, node.Id, rule.Folder.Value);
            return FilingOutcome.Blocked;
        }

        var filing = await db.NodeFilings.SingleOrDefaultAsync(f => f.NodeId == node.Id, ct);
        if (filing is null)
        {
            filing = new NodeFiling { NodeId = node.Id, Tag = rule.Tag.Value };
            db.NodeFilings.Add(filing);
        }

        var fromId = node.ParentId;
        var now = time.GetUtcNow();

        // A rule never hides a file from people it is shared with through the folders it is in (§13.6).
        var outcome =
            fromId == folder.Id ? FilingOutcome.AlreadyThere
            : await HidesFromSomeoneAsync(node, folder.Path, ct) ? FilingOutcome.Shared
            : FilingOutcome.Move;
        if (outcome == FilingOutcome.Shared)
        {
            // Drop the folders it would have made, and their audit events.
            foreach (
                var made in db
                    .ChangeTracker.Entries()
                    .Where(e => e.State == EntityState.Added && e.Entity is not NodeFiling)
                    .ToList()
            )
            {
                made.State = EntityState.Detached;
            }

            // A file a rule filed before keeps that filing; one it never filed is marked, so rules don't try again.
            if (db.Entry(filing).State == EntityState.Added)
            {
                filing.RuleId = rule.Id;
                filing.FromFolderId = fromId;
                filing.FiledAt = now;
                filing.Skipped = true;
                filing.RunId = runId;
            }

            await db.SaveChangesAsync(ct);
            await live.NotifyAsync(node.OwnerId, LiveTopics.Account, ct);
            await tx.CommitAsync(ct);
            LogSkipped(logger, node.Id, rule.Folder.Value);
            return outcome;
        }

        if (outcome == FilingOutcome.Move)
        {
            var details = new JsonObject
            {
                ["from"] = fromId?.ToString(),
                ["to"] = folder.Id.ToString(),
                ["rule"] = rule.Id.ToString(),
                ["tag"] = rule.Tag.Value,
            };
            if (runId is not null)
            {
                details["run"] = runId.ToString();
            }

            // A name already taken there gets a number, as an upload would: "kvitto (1).pdf".
            if (await drive.FindByNameAsync(node.OwnerId, folder.Id, node.Name, ct) is not null)
            {
                var name = await drive.FreeNameAsync(node.OwnerId, folder.Id, node.Name, ct);
                details["name"] = name.Value;
                node.Name = name;
            }

            node.PlaceUnder(folder);
            audit.AddSystem(AuditActions.NodeMove, node.Id, details: details);
        }

        // A file a run finds already in place keeps the filing that put it there, if any, so its Undo still works.
        if (outcome == FilingOutcome.Move || db.Entry(filing).State == EntityState.Added)
        {
            filing.RuleId = rule.Id;
            filing.Tag = rule.Tag.Value;
            filing.FromFolderId = fromId;
            filing.ToFolderId = folder.Id;
            filing.FiledAt = now;
            filing.UndoneAt = null;
            filing.Skipped = false;
            filing.RunId = runId;
        }
        else if (filing.Skipped || filing.PlacedByOwner(node.ParentId))
        {
            // It is where the rule would put it, so it counts as filed there again.
            filing.ToFolderId = folder.Id;
            filing.UndoneAt = null;
            filing.Skipped = false;
        }

        await db.SaveChangesAsync(ct);
        var topics = outcome == FilingOutcome.Move ? LiveTopics.Files : LiveTopics.None;
        if (runId is null)
        {
            topics |= LiveTopics.Filings;
        }

        if (topics != LiveTopics.None)
        {
            await live.NotifyAsync(node.OwnerId, topics, ct);
        }

        await tx.CommitAsync(ct);
        return outcome;
    }

    /// <summary>
    /// The file's date for <c>{year}</c> and <c>{month}</c>: the date taken people gave it, else when the photo was taken
    /// (on the server's clock), else when the file last changed on the device it came from, else when it was uploaded.
    /// </summary>
    internal async Task<DateTimeOffset> DateOfAsync(FileVersion version, CancellationToken ct) =>
        (await DatesAsync([version], ct))[version.Id];

    /// <summary><see cref="DateOfAsync"/> for many versions at once, by version id.</summary>
    internal async Task<Dictionary<Guid, DateTimeOffset>> DatesAsync(
        IReadOnlyCollection<FileVersion> versions,
        CancellationToken ct
    )
    {
        var ids = versions.Select(v => v.Id).Distinct().ToList();
        var edited = await db
            .VersionDetails.Where(d => ids.Contains(d.VersionId) && d.TakenAt != null)
            .Select(d => new { d.VersionId, d.TakenAt })
            .ToDictionaryAsync(d => d.VersionId, d => d.TakenAt!.Value, ct);
        var hashes = versions.Select(v => v.BlobHash).Distinct().ToList();
        var taken = (
            await db
                .BlobMetadata.Where(m => hashes.Contains(m.BlobHash) && m.TakenAt != null)
                .Select(m => new { m.BlobHash, m.TakenAt })
                .ToListAsync(ct)
        )
            .GroupBy(m => m.BlobHash)
            .ToDictionary(g => g.Key, g => g.First().TakenAt!.Value);

        var dates = new Dictionary<Guid, DateTimeOffset>();
        foreach (var version in versions)
        {
            // A date people gave the file is a wall-clock date already (§13.5), so "1974" stays in 1974.
            dates[version.Id] = edited.TryGetValue(version.Id, out var wall)
                ? new DateTimeOffset(wall.DateTime, TimeSpan.Zero)
                : TimeZoneInfo.ConvertTime(
                    taken.TryGetValue(version.BlobHash, out var at)
                        ? at
                        : version.ClientModifiedAt ?? version.UploadedAt,
                    time.LocalTimeZone
                );
        }

        return dates;
    }

    private Task<Node?> FindFolderAsync(
        Guid ownerId,
        Guid? parentId,
        NodeName name,
        CancellationToken ct
    ) => drive.FindByNameAsync(ownerId, parentId, name, ct);

    private Task<Node?> LiveFileAsync(Guid nodeId, CancellationToken ct) =>
        db
            .Nodes.Include(n => n.CurrentVersion)
            .SingleOrDefaultAsync(
                n =>
                    n.Id == nodeId
                    && n.Kind == NodeKind.File
                    && n.TrashedAt == null
                    && n.CurrentVersionId != null,
                ct
            );

    /// <summary>The owner's enabled rules in order, or only <paramref name="ruleId"/> when it is one of them.</summary>
    private Task<List<FilingRule>> RulesAsync(Guid ownerId, Guid? ruleId, CancellationToken ct) =>
        db
            .FilingRules.Where(r =>
                r.OwnerId == ownerId && r.Enabled && (ruleId == null || r.Id == ruleId)
            )
            .OrderBy(r => r.Position)
            .ToListAsync(ct);

    /// <summary>These nodes and everything in the folders among them, not in the trash.</summary>
    private async Task<List<Guid>> ScopeAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var list = ids.ToList();
        var paths = await db
            .Nodes.Where(n => list.Contains(n.Id) && n.TrashedAt == null)
            .Select(n => n.Path)
            .ToListAsync(ct);
        if (paths.Count == 0)
        {
            return [];
        }

        var array = paths.ToArray();
        return await db
            .Database.SqlQuery<Guid>(
                $"SELECT id AS \"Value\" FROM nodes WHERE trashed_at IS NULL AND path <@ ANY(CAST({array} AS ltree[]))"
            )
            .ToListAsync(ct);
    }

    /// <summary>
    /// Whether someone who sees the file through a folder above it couldn't see it under <paramref name="path"/>.
    /// Shares on the file itself go with it; new folders have none yet, so their shares are those of the folders above.
    /// </summary>
    private async Task<bool> HidesFromSomeoneAsync(Node node, string? path, CancellationToken ct)
    {
        if (node.ParentId is null)
        {
            return false;
        }

        var seeing = await GranteesAsync(node.Path[..node.Path.LastIndexOf('.')], ct);
        return seeing.Count > 0
            && !seeing.IsSubsetOf(path is null ? [] : await GranteesAsync(path, ct));
    }

    private async Task<HashSet<Guid>> GranteesAsync(string path, CancellationToken ct) =>
        [
            .. await db
                .Database.SqlQuery<Guid>(
                    $"""
                    SELECT DISTINCT s.grantee_user_id AS "Value" FROM shares s
                    JOIN nodes sn ON sn.id = s.node_id
                    WHERE sn.path @> {path}::ltree
                    """
                )
                .ToListAsync(ct),
        ];

    /// <summary>The folder at these names from the top of My files, making the ones missing. Null when a file has one of the names.</summary>
    private async Task<Node?> EnsureFoldersAsync(
        Guid ownerId,
        IReadOnlyList<NodeName> names,
        FilingRule rule,
        CancellationToken ct
    )
    {
        Node? parent = null;
        var made = false;
        var now = time.GetUtcNow();
        foreach (var name in names)
        {
            var existing = made ? null : await drive.FindByNameAsync(ownerId, parent?.Id, name, ct);
            if (existing is { IsFolder: false })
            {
                return null;
            }

            if (existing is null)
            {
                var folder = new Node
                {
                    OwnerId = ownerId,
                    Kind = NodeKind.Folder,
                    Name = name,
                    CreatedAt = now,
                    ModifiedAt = now,
                };
                folder.PlaceUnder(parent);
                db.Nodes.Add(folder);
                audit.AddSystem(
                    AuditActions.FolderCreate,
                    folder.Id,
                    details: new JsonObject { ["rule"] = rule.Id.ToString() }
                );
                existing = folder;
                made = true;
            }

            parent = existing;
        }

        return parent;
    }

    /// <summary>Finds the folders of a plan without making any, remembering what it found.</summary>
    private sealed class FolderLookup(Filer filer, Guid ownerId)
    {
        private readonly Dictionary<string, (Node?, bool, bool)> found = [];
        private readonly Dictionary<string, HashSet<Guid>> grantees = [];

        /// <summary>The deepest of the folders that exists, whether all do, and whether a file holds one of the names.</summary>
        public async Task<(Node? Folder, bool Complete, bool Blocked)> FindAsync(
            IReadOnlyList<NodeName> names,
            CancellationToken ct
        )
        {
            var key = string.Join("/", names.Select(n => n.Key));
            if (found.TryGetValue(key, out var hit))
            {
                return hit;
            }

            Node? parent = null;
            var result = (parent, true, false);
            foreach (var name in names)
            {
                var existing = await filer.FindFolderAsync(ownerId, parent?.Id, name, ct);
                if (existing is null)
                {
                    result = (parent, false, false);
                    break;
                }

                if (!existing.IsFolder)
                {
                    result = (parent, false, true);
                    break;
                }

                parent = existing;
                result = (parent, true, false);
            }

            return found[key] = result;
        }

        /// <summary>Whether filing the file under <paramref name="folder"/> (or new folders below it) hides it from someone.</summary>
        public async Task<bool> HidesAsync(Node node, Node? folder, CancellationToken ct)
        {
            if (node.ParentId is null)
            {
                return false;
            }

            var seeing = await GranteesAsync(node.Path[..node.Path.LastIndexOf('.')], ct);
            return seeing.Count > 0
                && !seeing.IsSubsetOf(folder is null ? [] : await GranteesAsync(folder.Path, ct));
        }

        private async Task<HashSet<Guid>> GranteesAsync(string path, CancellationToken ct) =>
            grantees.TryGetValue(path, out var hit)
                ? hit
                : grantees[path] = await filer.GranteesAsync(path, ct);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Not filing {NodeId}: a file has the name of a folder in \"{Folder}\""
    )]
    private static partial void LogBlocked(ILogger logger, Guid nodeId, string folder);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Not filing {NodeId} in \"{Folder}\": people it is shared with couldn't see it there"
    )]
    private static partial void LogSkipped(ILogger logger, Guid nodeId, string folder);
}
