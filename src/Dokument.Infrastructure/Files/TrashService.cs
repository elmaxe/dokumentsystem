using System.Text.Json.Nodes;
using Dokument.Application.Files;
using Dokument.Domain.Audit;
using Dokument.Domain.Files;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;

namespace Dokument.Infrastructure.Files;

/// <summary>
/// The trash (docs/design.md §12). Trashing marks a node and everything live below it with the same
/// <see cref="Node.TrashedRootId"/>; that group is what the Trash page shows as one item and what restore brings back.
/// </summary>
public sealed class TrashService(
    DokumentDbContext db,
    DriveService drive,
    PurgeService purge,
    PurgeSignal purgeSignal,
    AuditLog audit,
    SettingsStore settings,
    TimeProvider time
)
{
    /// <summary>The items people trashed, as opposed to the things that went with them.</summary>
    private IQueryable<Node> Roots(Actor actor) =>
        db.Nodes.VisibleTo(actor).Where(n => n.TrashedAt != null && n.TrashedRootId == n.Id);

    public async Task TrashAsync(
        Actor actor,
        IReadOnlyCollection<Guid> ids,
        CancellationToken ct = default
    )
    {
        var wanted = ids.Distinct().ToList();
        var nodes = await db
            .Nodes.VisibleTo(actor)
            .AsNoTracking()
            .Where(n => wanted.Contains(n.Id) && n.TrashedAt == null)
            .ToListAsync(ct);
        if (nodes.Count != wanted.Count)
        {
            throw DriveException.NotFound();
        }

        var now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var trashed = new List<Node>();

        // Folders before their contents: something selected inside a selected folder simply goes with it.
        foreach (var node in nodes.OrderBy(n => n.Path.Length))
        {
            if (trashed.Any(t => t.Contains(node)))
            {
                continue;
            }

            await db.Database.ExecuteSqlAsync(
                $"UPDATE nodes SET trashed_at = {now}, trashed_root_id = {node.Id} WHERE path <@ {node.Path}::ltree AND trashed_at IS NULL",
                ct
            );
            audit.Add(actor, AuditActions.NodeTrash, node.Id);
            trashed.Add(node);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>The Trash page, most recently trashed first, with each item's size and when the purge deletes it.</summary>
    public async Task<List<TrashEntry>> ListAsync(Actor actor, CancellationToken ct = default)
    {
        var keepFor = (await settings.GetAsync(SettingKeys.Trash, ct)).KeepFor;
        var roots = await Roots(actor)
            .AsNoTracking()
            .Include(n => n.CurrentVersion)
            .OrderByDescending(n => n.TrashedAt)
            .ToListAsync(ct);
        var contents = await db
            .Nodes.VisibleTo(actor)
            .Where(n => n.TrashedRootId != null && n.TrashedRootId != n.Id)
            .GroupBy(n => n.TrashedRootId!.Value)
            .Select(g => new
            {
                Root = g.Key,
                Count = g.Count(),
                Bytes = g.Sum(n => n.CurrentVersion == null ? 0 : n.CurrentVersion.Size),
            })
            .ToDictionaryAsync(g => g.Root, ct);
        return roots
            .Select(n =>
            {
                var inside = contents.GetValueOrDefault(n.Id);
                return new TrashEntry(
                    n,
                    inside?.Count ?? 0,
                    (n.CurrentVersion?.Size ?? 0) + (inside?.Bytes ?? 0),
                    n.TrashedAt!.Value + keepFor
                );
            })
            .ToList();
    }

    /// <summary>
    /// The storage that emptying the trash would free: blobs only trashed files use, each once (§17).
    /// </summary>
    public Task<long> FreeableBytesAsync(Guid ownerId, CancellationToken ct = default) =>
        db
            .Database.SqlQuery<long>(
                $"""
                SELECT COALESCE(sum(b.size), 0)::bigint AS "Value" FROM blobs b
                WHERE b.hash IN (
                    SELECT v.blob_hash FROM file_versions v JOIN nodes n ON n.id = v.node_id
                    WHERE n.owner_id = {ownerId} AND n.trashed_at IS NOT NULL)
                  AND NOT EXISTS (
                    SELECT 1 FROM file_versions v JOIN nodes n ON n.id = v.node_id
                    WHERE n.owner_id = {ownerId} AND n.trashed_at IS NULL AND v.blob_hash = b.hash)
                """
            )
            .SingleAsync(ct);

    /// <summary>
    /// Brings back each trash group where it came from. If that folder is itself in the trash, the item goes to the top
    /// of My files instead; if the name is taken there, it gets a number, as with Keep both.
    /// </summary>
    public async Task<List<RestoredNode>> RestoreAsync(
        Actor actor,
        IReadOnlyCollection<Guid> ids,
        CancellationToken ct = default
    )
    {
        var wanted = ids.Distinct().ToList();
        var roots = await Roots(actor).Where(n => wanted.Contains(n.Id)).ToListAsync(ct);
        if (roots.Count != wanted.Count)
        {
            throw DriveException.NotFound();
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var restored = new List<RestoredNode>();
        foreach (var root in roots.OrderBy(n => n.Path.Length))
        {
            var parent = root.ParentId is { } parentId
                ? await db.Nodes.AsNoTracking().SingleOrDefaultAsync(n => n.Id == parentId, ct)
                : null;
            var toRoot =
                parent is null && root.ParentId is not null || parent is { IsTrashed: true };
            var target = toRoot ? null : parent;
            var oldPath = root.Path;
            var oldName = root.Name;
            if (await drive.FindByNameAsync(root.OwnerId, target?.Id, root.Name, ct) is not null)
            {
                root.Name = await drive.FreeNameAsync(root.OwnerId, target?.Id, root.Name, ct);
            }

            if (toRoot)
            {
                root.PlaceUnder(null);
            }

            // Name and place first, then out of the trash, so the unique name index only ever sees the final name.
            await db.SaveChangesAsync(ct);
            if (toRoot && root.IsFolder)
            {
                await db.Database.ExecuteSqlAsync(
                    $"UPDATE nodes SET path = {root.Path}::ltree || subpath(path, nlevel({oldPath}::ltree)) WHERE path <@ {oldPath}::ltree AND id <> {root.Id}",
                    ct
                );
            }

            await db.Database.ExecuteSqlAsync(
                $"UPDATE nodes SET trashed_at = NULL, trashed_root_id = NULL WHERE trashed_root_id = {root.Id}",
                ct
            );
            db.Entry(root).State = EntityState.Detached;
            root.TrashedAt = null;
            root.TrashedRootId = null;

            var details = new JsonObject { ["to"] = target?.Id.ToString() };
            if (root.Name != oldName)
            {
                details["renamedTo"] = root.Name.Value;
            }

            audit.Add(actor, AuditActions.NodeRestore, root.Id, details: details);
            await db.SaveChangesAsync(ct);
            restored.Add(new RestoredNode(root, toRoot));
        }

        await tx.CommitAsync(ct);
        return restored;
    }

    /// <summary>"Delete forever" for items in the trash. The rows go now; the purge deletes the files within seconds.</summary>
    public async Task DeleteForeverAsync(
        Actor actor,
        IReadOnlyCollection<Guid> ids,
        CancellationToken ct = default
    )
    {
        var wanted = ids.Distinct().ToList();
        var roots = await Roots(actor)
            .AsNoTracking()
            .Where(n => wanted.Contains(n.Id))
            .ToListAsync(ct);
        if (roots.Count != wanted.Count)
        {
            throw DriveException.NotFound();
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await DeleteAsync(actor, roots, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        purgeSignal.Request();
    }

    /// <summary>Deletes everything in the user's trash forever. Returns how many items it held.</summary>
    public async Task<int> EmptyAsync(Actor actor, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var roots = await Roots(actor).AsNoTracking().ToListAsync(ct);
        await DeleteAsync(actor, roots, ct);
        audit.Add(
            actor,
            AuditActions.TrashEmpty,
            details: new JsonObject { ["items"] = roots.Count }
        );
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        purgeSignal.Request();
        return roots.Count;
    }

    private async Task DeleteAsync(Actor actor, List<Node> roots, CancellationToken ct)
    {
        var deleted = new List<Node>();

        // A group trashed earlier inside a folder that was trashed later goes with that folder.
        foreach (var root in roots.OrderBy(n => n.Path.Length))
        {
            if (deleted.Any(d => d.Contains(root)))
            {
                continue;
            }

            var count = await purge.DeleteTreeAsync(root, ct);
            audit.Add(
                actor,
                AuditActions.NodeDeleteForever,
                root.Id,
                details: new JsonObject { ["name"] = root.Name.Value, ["nodes"] = count }
            );
            deleted.Add(root);
        }
    }
}

/// <param name="ItemCount">How many things went to the trash with it (a folder's contents).</param>
/// <param name="Bytes">The current versions' sizes, the folder's contents included.</param>
/// <param name="DeleteAt">When the purge deletes it forever.</param>
public sealed record TrashEntry(Node Node, int ItemCount, long Bytes, DateTimeOffset DeleteAt);

/// <param name="MovedToRoot">The folder it was in is in the trash, so it went to the top of My files instead.</param>
public sealed record RestoredNode(Node Node, bool MovedToRoot);
