using System.Text.Json.Nodes;
using Dokument.Application.Files;
using Dokument.Domain.Audit;
using Dokument.Domain.Files;
using Dokument.Domain.Identity;
using Dokument.Domain.Sharing;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Live;
using Dokument.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dokument.Infrastructure.Sharing;

/// <summary>
/// Sharing files and folders with people (docs/design.md §9). Only the owner shares; a share on a folder covers what is
/// inside it, and a share lower down can only add to what a folder above already gives.
/// </summary>
public sealed class ShareService(
    DokumentDbContext db,
    DriveService drive,
    AuditLog audit,
    LiveChanges live,
    TimeProvider time
)
{
    /// <summary>Who can see a node and why. Anyone who can see it may ask; only the owner may change it.</summary>
    public async Task<NodeAccessInfo> AccessAsync(
        Actor actor,
        Guid nodeId,
        CancellationToken ct = default
    )
    {
        var node = await drive.GetAsync(actor, nodeId, ct);
        var yours = await drive.RoleAsync(actor, node, ct) ?? Access.View;
        var chain = ChainOf(node);
        var shares = await db
            .Shares.AsNoTracking()
            .Where(s => chain.Contains(s.NodeId))
            .ToListAsync(ct);
        var userIds = shares.Select(s => s.GranteeUserId).Append(node.OwnerId).Distinct().ToList();
        var users = await db
            .Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);

        // An inherited role names the folder it comes from, unless the person asking can't see that folder.
        var fromIds = shares
            .Where(s => s.NodeId != node.Id)
            .Select(s => s.NodeId)
            .Distinct()
            .ToList();
        var folders = await db
            .Nodes.VisibleTo(actor)
            .AsNoTracking()
            .Where(n => fromIds.Contains(n.Id))
            .ToDictionaryAsync(n => n.Id, ct);

        var people = shares
            .GroupBy(s => s.GranteeUserId)
            .Select(g =>
            {
                var direct = g.SingleOrDefault(s => s.NodeId == node.Id);
                var inherited = g.Where(s => s.NodeId != node.Id)
                    .OrderByDescending(s => s.Role)
                    .ThenByDescending(s => chain.IndexOf(s.NodeId))
                    .FirstOrDefault();
                var role = (ShareRole)
                    Math.Max((int)(direct?.Role ?? 0), (int)(inherited?.Role ?? 0));
                return new AccessEntry(
                    users[g.Key],
                    role,
                    direct,
                    inherited?.Role,
                    inherited is null ? null : folders.GetValueOrDefault(inherited.NodeId)
                );
            })
            .OrderBy(e => e.Person.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new NodeAccessInfo(node, users[node.OwnerId], yours, people);
    }

    /// <summary>Shares a node with someone, or changes the role they have on it directly.</summary>
    public async Task<Share> GrantAsync(
        Actor actor,
        Guid nodeId,
        Guid userId,
        ShareRole role,
        CancellationToken ct = default
    )
    {
        var node = await drive.GetAsync(actor, nodeId, ct, Access.Own);
        var person =
            await db.Users.SingleOrDefaultAsync(u => u.Id == userId && u.DisabledAt == null, ct)
            ?? throw DriveException.NotFound("person");
        if (person.Id == node.OwnerId)
        {
            throw new DriveException(DriveError.InvalidRequest, "You already own this.");
        }

        var share = await db.Shares.SingleOrDefaultAsync(
            s => s.NodeId == node.Id && s.GranteeUserId == userId,
            ct
        );
        if (share?.Role == role)
        {
            return share;
        }

        await EnsureAboveInheritedAsync(node, person, role, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (share is null)
        {
            share = new Share
            {
                NodeId = node.Id,
                GranteeUserId = userId,
                Role = role,
                CreatedBy = actor.UserId,
                CreatedAt = time.GetUtcNow(),
            };
            db.Shares.Add(share);
            audit.Add(actor, AuditActions.ShareGrant, node.Id, details: Details(person, role));
        }
        else
        {
            var details = Details(person, role);
            details["from"] = Name(share.Role);
            share.Role = role;
            audit.Add(actor, AuditActions.ShareChange, node.Id, details: details);
        }

        await db.SaveChangesAsync(ct);
        await live.NotifyAsync([node.OwnerId, userId], LiveTopics.Shares | LiveTopics.Files, ct);
        await tx.CommitAsync(ct);
        return share;
    }

    /// <summary>Changes the role of a share the person owns the node of.</summary>
    public async Task<Share> ChangeAsync(
        Actor actor,
        Guid shareId,
        ShareRole role,
        CancellationToken ct = default
    )
    {
        var share =
            await db.Shares.AsNoTracking().SingleOrDefaultAsync(s => s.Id == shareId, ct)
            ?? throw DriveException.NotFound("share");
        return await GrantAsync(actor, share.NodeId, share.GranteeUserId, role, ct);
    }

    /// <summary>
    /// Ends a share: the owner removing someone, or the person themselves leaving. Their stars and Recent entries for
    /// what they can no longer see go with it.
    /// </summary>
    public async Task RemoveAsync(Actor actor, Guid shareId, CancellationToken ct = default)
    {
        var share =
            await db.Shares.SingleOrDefaultAsync(s => s.Id == shareId, ct)
            ?? throw DriveException.NotFound("share");
        var leaving = share.GranteeUserId == actor.UserId;
        var node = await drive.GetAsync(
            actor,
            share.NodeId,
            ct,
            leaving ? Access.View : Access.Own
        );
        var person = await db.Users.SingleAsync(u => u.Id == share.GranteeUserId, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Shares.Remove(share);
        audit.Add(
            actor,
            leaving ? AuditActions.ShareLeave : AuditActions.ShareRevoke,
            node.Id,
            details: Details(person, share.Role)
        );
        await db.SaveChangesAsync(ct);
        await ForgetUnseenAsync(person.Id, ct);
        await live.NotifyAsync([node.OwnerId, person.Id], LiveTopics.Shares | LiveTopics.Files, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>"Leave" from Shared with me: ends the person's own share on the node.</summary>
    public async Task LeaveAsync(Actor actor, Guid nodeId, CancellationToken ct = default)
    {
        var shareId =
            await db
                .Shares.Where(s => s.NodeId == nodeId && s.GranteeUserId == actor.UserId)
                .Select(s => (Guid?)s.Id)
                .SingleOrDefaultAsync(ct)
            ?? throw DriveException.NotFound("share");
        await RemoveAsync(actor, shareId, ct);
    }

    /// <summary>
    /// Shared with me: the top-most live items shared with the person. A folder inside one already listed is left
    /// out, since it is reached from there.
    /// </summary>
    public async Task<List<SharedItem>> SharedWithMeAsync(
        Actor actor,
        CancellationToken ct = default
    )
    {
        var rows = await db
            .Shares.AsNoTracking()
            .Where(s => s.GranteeUserId == actor.UserId)
            .Join(
                db.Nodes.Include(n => n.CurrentVersion),
                s => s.NodeId,
                n => n.Id,
                (s, n) => new { Share = s, Node = n }
            )
            .Where(r => r.Node.TrashedAt == null && r.Node.OwnerId != actor.UserId)
            .ToListAsync(ct);
        var paths = rows.Select(r => r.Node.Path).ToList();
        return rows.Where(r =>
                !paths.Any(p => r.Node.Path.StartsWith(p + ".", StringComparison.Ordinal))
            )
            .OrderByDescending(r => r.Share.CreatedAt)
            .Select(r => new SharedItem(r.Node, r.Share))
            .ToList();
    }

    /// <summary>The person opened something shared with them, so it no longer shows as New.</summary>
    public async Task MarkSeenAsync(Actor actor, Guid nodeId, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var changed = await db
            .Shares.Where(s =>
                s.NodeId == nodeId && s.GranteeUserId == actor.UserId && s.SeenAt == null
            )
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SeenAt, now), ct);
        if (changed > 0)
        {
            await live.NotifyAsync(actor.UserId, LiveTopics.Shares, ct);
        }
    }

    /// <summary>The people someone can share with: everyone who has signed in and isn't disabled, but themselves.</summary>
    public async Task<List<User>> PeopleAsync(
        Actor actor,
        string? query,
        CancellationToken ct = default
    )
    {
        var people = db
            .Users.AsNoTracking()
            .Where(u => u.DisabledAt == null && u.Id != actor.UserId);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var pattern = $"%{EscapeLike(query.Trim())}%";
            people = people.Where(u =>
                EF.Functions.ILike(u.DisplayName, pattern, "\\")
                || (u.Email != null && EF.Functions.ILike(u.Email, pattern, "\\"))
            );
        }

        return await people.OrderBy(u => u.DisplayName).Take(50).ToListAsync(ct);
    }

    /// <summary>
    /// Who would stop seeing these items if they moved to <paramref name="folderId"/> (null: the top of the person's
    /// own My files): people a folder above them is shared with, unless the new folder is shared with them too or the
    /// item itself is. The owner never loses access.
    /// </summary>
    public async Task<List<User>> LosingAccessAsync(
        Actor actor,
        IReadOnlyCollection<Guid> ids,
        Guid? folderId,
        CancellationToken ct = default
    )
    {
        var nodes = new List<Node>();
        foreach (var id in ids.Distinct())
        {
            nodes.Add(await drive.GetAsync(actor, id, ct));
        }

        var target = await drive.GetFolderAsync(actor, folderId, ct);
        var after = target is null ? [] : await GranteesAsync(ChainOf(target), ct);
        var losing = new HashSet<Guid>();
        foreach (var node in nodes)
        {
            var above = ChainOf(node)[..^1];
            var before = await GranteesAsync(above, ct);
            var own = await GranteesAsync([node.Id], ct);
            losing.UnionWith(before.Except(after).Except(own).Where(id => id != node.OwnerId));
        }

        return await db
            .Users.AsNoTracking()
            .Where(u => losing.Contains(u.Id))
            .OrderBy(u => u.DisplayName)
            .ToListAsync(ct);
    }

    private async Task<HashSet<Guid>> GranteesAsync(
        IReadOnlyCollection<Guid> nodeIds,
        CancellationToken ct
    ) =>
        [
            .. await db
                .Shares.Where(s => nodeIds.Contains(s.NodeId))
                .Select(s => s.GranteeUserId)
                .ToListAsync(ct),
        ];

    /// <summary>A share lower down can only add: it must give more than the folders above already do.</summary>
    private async Task EnsureAboveInheritedAsync(
        Node node,
        User person,
        ShareRole role,
        CancellationToken ct
    )
    {
        var above = ChainOf(node)[..^1];
        var inherited = await db
            .Shares.Where(s => above.Contains(s.NodeId) && s.GranteeUserId == person.Id)
            .MaxAsync(s => (ShareRole?)s.Role, ct);
        if (inherited >= role)
        {
            throw new DriveException(
                DriveError.InvalidRequest,
                $"{person.DisplayName} is already {(inherited == ShareRole.Editor ? "an editor" : "a viewer")} through a folder above this one."
            );
        }
    }

    /// <summary>Drops the person's stars and Recent entries for things they can no longer see.</summary>
    private async Task ForgetUnseenAsync(Guid userId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync(
            $"DELETE FROM stars s USING nodes n WHERE s.node_id = n.id AND s.user_id = {userId} AND n.owner_id <> {userId} AND shared_role({userId}, n.path) = 0",
            ct
        );
        await db.Database.ExecuteSqlAsync(
            $"DELETE FROM recent_files r USING nodes n WHERE r.node_id = n.id AND r.user_id = {userId} AND n.owner_id <> {userId} AND shared_role({userId}, n.path) = 0",
            ct
        );
    }

    /// <summary>The ids of the folders above a node and the node itself, top first.</summary>
    private static List<Guid> ChainOf(Node node) =>
        node.Path.Split('.').Select(label => Guid.ParseExact(label, "N")).ToList();

    private static JsonObject Details(User person, ShareRole role) =>
        new() { ["user"] = person.Id.ToString(), ["role"] = Name(role) };

    private static string Name(ShareRole role) => role.ToString().ToLowerInvariant();

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}

/// <param name="YourAccess">What the person asking may do with it.</param>
/// <param name="People">Everyone it is shared with, through this node or a folder above it. The owner isn't listed.</param>
public sealed record NodeAccessInfo(
    Node Node,
    User Owner,
    Access YourAccess,
    List<AccessEntry> People
);

/// <param name="Role">What they can do: the higher of the direct and the inherited role.</param>
/// <param name="Direct">The share on this node itself, which the owner can change or remove.</param>
/// <param name="InheritedRole">What a folder above gives them, the least this node's share may be.</param>
/// <param name="InheritedFrom">That folder, when the person asking can see it.</param>
public sealed record AccessEntry(
    User Person,
    ShareRole Role,
    Share? Direct,
    ShareRole? InheritedRole,
    Node? InheritedFrom
);

public sealed record SharedItem(Node Node, Share Share);
