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
/// <param name="Running">A run of the rule, or of all the person's rules, is going.</param>
/// <param name="Skipped">Files the rule left where they were, because the folder would hide them from people they are shared with.</param>
public sealed record FilingRuleSummary(FilingRule Rule, int Filed, int Skipped, bool Running);

/// <param name="Folder">Where it would go, as "Kvitton / 2026".</param>
public sealed record FilingExample(Guid NodeId, string Name, string? MimeType, string Folder);

/// <param name="Files">The person's files that have the tag now and that no rule has filed.</param>
public sealed record FilingPreview(int Files, IReadOnlyList<FilingExample> Examples);

/// <param name="From">Where it is now, as "Inkorg / Scans", or "" for the top of My files.</param>
public sealed record RunFile(Guid NodeId, string Name, string? MimeType, string From);

/// <param name="Folder">Where they'd go, as "Kvitton / 2026".</param>
/// <param name="NewFolder">The folder, or one above it, would be made.</param>
/// <param name="Files">The first of them, at most <see cref="FilingService.GroupFiles"/>.</param>
public sealed record RunGroup(string Folder, bool NewFolder, int Count, List<RunFile> Files);

/// <summary>What a run of filing rules would do (docs/design.md §13.6).</summary>
/// <param name="Move">Files that would move, in <paramref name="Groups"/> by folder.</param>
/// <param name="AlreadyThere">Files already in their rule's folder.</param>
/// <param name="Placed">Files the person put somewhere after a rule filed them, or moved back with Undo, which stay.</param>
/// <param name="Shared">Files that stay because the folder would hide them from people they are shared with.</param>
/// <param name="Blocked">Files that stay because a file has the name of one of the folders.</param>
/// <param name="NoRule">The person's files in scope without a tag of the run's rules.</param>
/// <param name="NotYours">Files in scope that someone else owns.</param>
public sealed record RunPreview(
    int Move,
    List<RunGroup> Groups,
    int AlreadyThere,
    int Placed,
    int Shared,
    int Blocked,
    int NoRule,
    int NotYours
);

/// <param name="RuleTag">The tag of the one rule it runs, or null for all rules (or a rule deleted since).</param>
/// <param name="Folders">The folders it moved files to.</param>
/// <param name="Folder">The one folder it moved files to, as "Kvitton / 2026", when there is one.</param>
/// <param name="CanUndo">It has finished, moved files, wasn't undone, and is recent enough to undo.</param>
public sealed record FilingRunSummary(
    FilingRun Run,
    string? RuleTag,
    int Folders,
    Guid? FolderId,
    string? Folder,
    bool CanUndo
);

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

    /// <summary>Files a run's preview lists per folder.</summary>
    public const int GroupFiles = 100;

    /// <summary>Files and folders a run can be asked to look at by id.</summary>
    public const int MaxRunNodes = 1000;

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
        var counts = await db
            .NodeFilings.Where(f => ids.Contains(f.RuleId) && f.UndoneAt == null)
            .GroupBy(f => new { f.RuleId, f.Skipped })
            .Select(g => new
            {
                g.Key.RuleId,
                g.Key.Skipped,
                Count = g.Count(),
            })
            .ToListAsync(ct);
        var filed = counts.Where(c => !c.Skipped).ToDictionary(c => c.RuleId!.Value, c => c.Count);
        var skipped = counts.Where(c => c.Skipped).ToDictionary(c => c.RuleId!.Value, c => c.Count);
        var active = await db
            .FilingRuns.Where(r =>
                r.OwnerId == actor.UserId
                && (r.Status == FilingRunStatus.Queued || r.Status == FilingRunStatus.Running)
            )
            .Select(r => r.RuleId)
            .ToListAsync(ct);
        return rules
            .Select(r => new FilingRuleSummary(
                r,
                filed.GetValueOrDefault(r.Id),
                skipped.GetValueOrDefault(r.Id),
                r.Enabled && active.Any(id => id == null || id == r.Id)
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
            await QueueRunAsync(actor, rule.Id, null, false, ct);
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
            await QueueRunAsync(actor, rule.Id, null, false, ct);
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
            .NodeFilings.Where(f =>
                f.FiledAt > since && f.UndoneAt == null && !f.Skipped && f.RunId == null
            )
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
                ? await db.NodeFilings.SingleOrDefaultAsync(
                    f => f.NodeId == nodeId && !f.Skipped,
                    ct
                )
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

        return await UndoFilingAsync(actor, node, filing, ct);
    }

    /// <summary>Moves a filed file back where it was, or to the top of My files if that folder is gone, and marks it undone.</summary>
    private async Task<Node> UndoFilingAsync(
        Actor actor,
        Node node,
        NodeFiling filing,
        CancellationToken ct
    )
    {
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

        return await drive.MoveAsync(actor, node.Id, back, ct);
    }

    /// <summary>
    /// What running <paramref name="ruleId"/> (or all the person's rules that are on) over <paramref name="nodeIds"/>
    /// (files and folders, or all their files) would do. Changes nothing.
    /// </summary>
    public async Task<RunPreview> PreviewRunAsync(
        Actor actor,
        Guid? ruleId,
        IReadOnlyCollection<Guid>? nodeIds,
        bool includePlaced,
        CancellationToken ct = default
    )
    {
        await CheckRunAsync(actor, ruleId, nodeIds, ct);
        var plan = await filer.PlanAsync(actor.UserId, ruleId, nodeIds, includePlaced, ct);
        var moving = plan.Items.Where(i => i.Outcome == FilingOutcome.Move).ToList();
        var groups = moving
            .GroupBy(i => string.Join(" / ", i.Folder.Select(n => n.Value)))
            .Select(g => (Folder: g.Key, Items: g.ToList()))
            .OrderBy(g => g.Folder, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var froms = await FolderNamesAsync(
            groups
                .SelectMany(g => g.Items.Take(GroupFiles))
                .Select(i => i.FromFolderId)
                .OfType<Guid>()
                .Distinct()
                .ToList(),
            ct
        );
        int Count(FilingOutcome outcome) => plan.Items.Count(i => i.Outcome == outcome);
        return new RunPreview(
            moving.Count,
            groups
                .Select(g => new RunGroup(
                    g.Folder,
                    g.Items.Any(i => i.NewFolder),
                    g.Items.Count,
                    g.Items.Take(GroupFiles)
                        .Select(i => new RunFile(
                            i.NodeId,
                            i.Name,
                            i.MimeType,
                            i.FromFolderId is { } from ? froms.GetValueOrDefault(from, "") : ""
                        ))
                        .ToList()
                ))
                .ToList(),
            Count(FilingOutcome.AlreadyThere),
            Count(FilingOutcome.Placed),
            Count(FilingOutcome.Shared),
            Count(FilingOutcome.Blocked),
            plan.NoRule,
            plan.NotYours
        );
    }

    /// <summary>Starts a run in the background, once the person has seen its preview. One run at a time per person.</summary>
    public async Task<FilingRun> StartRunAsync(
        Actor actor,
        Guid? ruleId,
        IReadOnlyCollection<Guid>? nodeIds,
        bool includePlaced,
        CancellationToken ct = default
    )
    {
        await CheckRunAsync(actor, ruleId, nodeIds, ct);
        if (
            await db.FilingRuns.AnyAsync(
                r =>
                    r.OwnerId == actor.UserId
                    && (r.Status == FilingRunStatus.Queued || r.Status == FilingRunStatus.Running),
                ct
            )
        )
        {
            throw new DriveException(
                DriveError.InvalidRequest,
                "Your filing rules are running already. Wait for them to finish, or stop them."
            );
        }

        var run = await QueueRunAsync(actor, ruleId, nodeIds, includePlaced, ct);
        await SaveAsync(actor, ct);
        return run;
    }

    /// <summary>Runs a run's rules on the files it covers. Run by the worker; stops between files when asked.</summary>
    public async Task RunAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await db.FilingRuns.SingleOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null || !run.Active)
        {
            return;
        }

        var stopped = run.StopRequested;
        if (!stopped)
        {
            run.Status = FilingRunStatus.Running;
            await db.SaveChangesAsync(ct);
            var plan = await filer.PlanAsync(
                run.OwnerId,
                run.RuleId,
                run.NodeIds,
                run.IncludePlaced,
                ct
            );
            var moving = plan.Items.Where(i => i.Outcome == FilingOutcome.Move).ToList();
            run.Total = moving.Count;
            await db.SaveChangesAsync(ct);
            foreach (var item in moving)
            {
                if (
                    await db
                        .FilingRuns.AsNoTracking()
                        .AnyAsync(r => r.Id == runId && r.StopRequested, ct)
                )
                {
                    stopped = true;
                    break;
                }

                var outcome = await filer.FileForRunAsync(item.NodeId, item.Rule, run, ct);
                run.Done++;
                if (outcome == FilingOutcome.Move)
                {
                    run.Moved++;
                }

                await db.SaveChangesAsync(ct);
            }
        }

        run.Status = stopped ? FilingRunStatus.Stopped : FilingRunStatus.Done;
        run.FinishedAt = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        await live.NotifyAsync(run.OwnerId, LiveTopics.Account | LiveTopics.Filings, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>A run of the person's, or their latest one when <paramref name="id"/> is null (null if they have none).</summary>
    public async Task<FilingRunSummary?> GetRunAsync(
        Actor actor,
        Guid? id,
        CancellationToken ct = default
    )
    {
        var runs = db.FilingRuns.AsNoTracking().Where(r => r.OwnerId == actor.UserId);
        var run = id is { } runId
            ? await runs.SingleOrDefaultAsync(r => r.Id == runId, ct)
                ?? throw DriveException.NotFound("run")
            : await runs.OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
        if (run is null)
        {
            return null;
        }

        var tag = run.RuleId is { } ruleId
            ? (await db.FilingRules.AsNoTracking().SingleOrDefaultAsync(r => r.Id == ruleId, ct))
                ?.Tag
                .Value
            : null;
        var folders = await db
            .NodeFilings.Where(f =>
                f.RunId == run.Id && !f.Skipped && f.FromFolderId != f.ToFolderId
            )
            .Select(f => f.ToFolderId)
            .Distinct()
            .ToListAsync(ct);
        var folderId = folders.Count == 1 ? folders[0] : null;
        var folder = folderId is { } one
            ? (await FolderNamesAsync([one], ct)).GetValueOrDefault(one)
            : null;
        var canUndo =
            !run.Active
            && run.Moved > 0
            && run.UndoneAt is null
            && run.FinishedAt > time.GetUtcNow() - FilingRun.UndoWindow;
        return new FilingRunSummary(run, tag, folders.Count, folderId, folder, canUndo);
    }

    /// <summary>Asks a run to stop after the file it is on. What it moved stays moved, and can be undone.</summary>
    public async Task StopRunAsync(Actor actor, Guid id, CancellationToken ct = default)
    {
        var run = await FindRunAsync(actor, id, ct);
        if (run.Active)
        {
            run.StopRequested = true;
            await SaveAsync(actor, ct);
        }
    }

    /// <summary>
    /// Moves the files a run moved back where they were before it, unless they have been moved since. Returns how many
    /// went back.
    /// </summary>
    public async Task<int> UndoRunAsync(Actor actor, Guid id, CancellationToken ct = default)
    {
        var run = await FindRunAsync(actor, id, ct);
        if (run.Active)
        {
            throw new DriveException(
                DriveError.InvalidRequest,
                "Wait for the run to finish, or stop it, before undoing it."
            );
        }

        if (run.UndoneAt is not null)
        {
            return 0;
        }

        var filings = await db
            .NodeFilings.Where(f =>
                f.RunId == run.Id
                && !f.Skipped
                && f.UndoneAt == null
                && f.FromFolderId != f.ToFolderId
            )
            .ToListAsync(ct);
        var back = 0;
        foreach (var filing in filings)
        {
            var node = await db
                .Nodes.Include(n => n.CurrentVersion)
                .SingleOrDefaultAsync(
                    n => n.Id == filing.NodeId && n.TrashedAt == null && n.OwnerId == actor.UserId,
                    ct
                );
            if (node is null || node.ParentId != filing.ToFolderId)
            {
                continue;
            }

            try
            {
                await UndoFilingAsync(actor, node, filing, ct);
                back++;
            }
            catch (DriveException)
            {
                // A name taken in the old folder since: that file stays where the run put it.
                filing.UndoneAt = null;
            }
        }

        run.UndoneAt = time.GetUtcNow();
        audit.Add(
            actor,
            AuditActions.FilingRuleChange,
            details: new JsonObject
            {
                ["change"] = "undo-run",
                ["run"] = run.Id.ToString(),
                ["files"] = back,
            }
        );
        await SaveAsync(actor, ct);
        return back;
    }

    /// <summary>Adds a run and its job to the unit of work. The caller saves.</summary>
    private async Task<FilingRun> QueueRunAsync(
        Actor actor,
        Guid? ruleId,
        IReadOnlyCollection<Guid>? nodeIds,
        bool includePlaced,
        CancellationToken ct
    )
    {
        var run = new FilingRun
        {
            OwnerId = actor.UserId,
            RuleId = ruleId,
            NodeIds = nodeIds?.Distinct().ToArray(),
            IncludePlaced = includePlaced,
            Status = FilingRunStatus.Queued,
            CreatedAt = time.GetUtcNow(),
        };
        db.FilingRuns.Add(run);
        audit.Add(
            actor,
            AuditActions.FilingRuleChange,
            details: new JsonObject
            {
                ["change"] = "run",
                ["run"] = run.Id.ToString(),
                ["rule"] = ruleId?.ToString(),
                ["nodes"] = nodeIds?.Count,
                ["includePlaced"] = includePlaced,
            }
        );
        await jobs.EnqueueAsync(JobType.FilingRun, run.Id.ToString(), JobPriority.Interactive, ct);
        return run;
    }

    /// <summary>Checks that the rule is the person's and on, and that they can see every node, before a run or its preview.</summary>
    private async Task CheckRunAsync(
        Actor actor,
        Guid? ruleId,
        IReadOnlyCollection<Guid>? nodeIds,
        CancellationToken ct
    )
    {
        if (ruleId is { } id)
        {
            if (!(await FindAsync(actor, id, ct)).Enabled)
            {
                throw new DriveException(
                    DriveError.InvalidRequest,
                    "Turn the rule on before running it."
                );
            }
        }
        else if (!await db.FilingRules.AnyAsync(r => r.OwnerId == actor.UserId && r.Enabled, ct))
        {
            throw new DriveException(
                DriveError.InvalidRequest,
                "You have no filing rules that are on."
            );
        }

        if (nodeIds is null)
        {
            return;
        }

        if (nodeIds.Count is 0 or > MaxRunNodes)
        {
            throw new DriveException(
                DriveError.InvalidRequest,
                $"Choose between 1 and {MaxRunNodes} files and folders."
            );
        }

        foreach (var nodeId in nodeIds.Distinct())
        {
            await drive.GetAsync(actor, nodeId, ct);
        }
    }

    private async Task<FilingRun> FindRunAsync(Actor actor, Guid id, CancellationToken ct) =>
        await db.FilingRuns.SingleOrDefaultAsync(r => r.Id == id && r.OwnerId == actor.UserId, ct)
        ?? throw DriveException.NotFound("run");

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
