using Dokument.Application.Files;
using Dokument.Domain.Files;
using Dokument.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dokument.Infrastructure.Files;

/// <summary>
/// What each person did last with their files, for the Recent page (docs/design.md §15.2): opened, downloaded,
/// uploaded or restored a version. Renames and moves don't count. Private to that person and separate from the audit
/// log, which never records preview opens (§18).
/// </summary>
public sealed class RecentFiles(DokumentDbContext db, TimeProvider time)
{
    /// <summary>How many files the Recent page shows.</summary>
    public const int Limit = 100;

    /// <summary>Notes the activity at once, inside the caller's transaction if there is one.</summary>
    public async Task RecordAsync(
        Guid userId,
        Guid nodeId,
        RecentAction action,
        CancellationToken ct = default
    )
    {
        var now = time.GetUtcNow();
        var name = action.ToString().ToLowerInvariant();
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO recent_files (user_id, node_id, at, action) VALUES ({userId}, {nodeId}, {now}, {name})
            ON CONFLICT (user_id, node_id) DO UPDATE SET at = excluded.at, action = excluded.action
            """,
            ct
        );
    }

    /// <summary>This person's most recent live files, newest first.</summary>
    public async Task<List<RecentEntry>> ListAsync(Actor actor, CancellationToken ct = default)
    {
        var rows = await db
            .RecentFiles.AsNoTracking()
            .Where(r => r.UserId == actor.UserId)
            .Join(
                db.Nodes.VisibleTo(actor)
                    .Where(n => n.TrashedAt == null && n.Kind == NodeKind.File)
                    .Include(n => n.CurrentVersion),
                r => r.NodeId,
                n => n.Id,
                (r, n) => new { Recent = r, Node = n }
            )
            .OrderByDescending(x => x.Recent.At)
            .Take(Limit)
            .ToListAsync(ct);
        return rows.Select(x => new RecentEntry(x.Node, x.Recent.At, x.Recent.Action)).ToList();
    }
}

public sealed record RecentEntry(Node Node, DateTimeOffset At, RecentAction Action);
