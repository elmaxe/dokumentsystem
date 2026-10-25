using Dokument.Application.Files;
using Dokument.Domain.Files;
using Dokument.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dokument.Infrastructure.Files;

/// <summary>
/// Each person's stars and the Starred page (docs/design.md §15.2). Trashed items keep their star but drop out of the
/// page until restored; deleting forever removes it with the node.
/// </summary>
public sealed class StarService(DokumentDbContext db, TimeProvider time)
{
    private IQueryable<Node> Live(Actor actor) =>
        db.Nodes.VisibleTo(actor).Where(n => n.TrashedAt == null);

    /// <summary>Stars or unstars these items. Starring twice, or unstarring what isn't starred, changes nothing.</summary>
    public async Task SetAsync(
        Actor actor,
        IReadOnlyCollection<Guid> ids,
        bool starred,
        CancellationToken ct = default
    )
    {
        var wanted = ids.Distinct().ToList();
        if (wanted.Count == 0 || wanted.Count > 1000)
        {
            throw new DriveException(DriveError.InvalidRequest, "Give 1 to 1000 ids.");
        }

        if (await Live(actor).CountAsync(n => wanted.Contains(n.Id), ct) != wanted.Count)
        {
            throw DriveException.NotFound();
        }

        if (starred)
        {
            var now = time.GetUtcNow();
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO stars (user_id, node_id, starred_at) SELECT {actor.UserId}, id, {now} FROM unnest({wanted.ToArray()}) AS id ON CONFLICT DO NOTHING",
                ct
            );
        }
        else
        {
            await db
                .Stars.Where(s => s.UserId == actor.UserId && wanted.Contains(s.NodeId))
                .ExecuteDeleteAsync(ct);
        }
    }

    /// <summary>The live items this person starred, in no particular order: the page sorts them.</summary>
    public async Task<List<Node>> ListAsync(Actor actor, CancellationToken ct = default) =>
        await Live(actor)
            .Include(n => n.CurrentVersion)
            .Where(n => db.Stars.Any(s => s.UserId == actor.UserId && s.NodeId == n.Id))
            .ToListAsync(ct);

    /// <summary>Which of these nodes the person starred.</summary>
    public async Task<HashSet<Guid>> StarredAsync(
        Guid userId,
        IReadOnlyCollection<Guid> nodeIds,
        CancellationToken ct = default
    ) =>
        (
            await db
                .Stars.Where(s => s.UserId == userId && nodeIds.Contains(s.NodeId))
                .Select(s => s.NodeId)
                .ToListAsync(ct)
        ).ToHashSet();
}
