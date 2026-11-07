using System.Text.Json.Nodes;
using Dokument.Application.Files;
using Dokument.Domain.Audit;
using Dokument.Domain.Files;
using Dokument.Domain.Identity;
using Dokument.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dokument.Infrastructure.Files;

/// <summary>
/// What happened to a file, or to a folder and everything in it, for the details panel's Activity tab (docs/design.md
/// §18). It reads the audit log. Everyone who can see the node sees its changes; downloads are shown to the owner, and
/// to each person for their own.
/// </summary>
public sealed class ActivityService(DokumentDbContext db, DriveService drive)
{
    public const int MaxLimit = 100;

    /// <summary>What shows in the tab: the changes people make, not admin or account events.</summary>
    private static readonly string[] Shown =
    [
        AuditActions.FolderCreate,
        AuditActions.FileUpload,
        AuditActions.FileCopy,
        AuditActions.PdfMerge,
        AuditActions.PdfSplit,
        AuditActions.VersionUpload,
        AuditActions.VersionRestore,
        AuditActions.VersionKeepForever,
        AuditActions.NodeRename,
        AuditActions.NodeMove,
        AuditActions.FileDetailsEdit,
        AuditActions.FileTextEdit,
        AuditActions.FileDownload,
        AuditActions.NodeTrash,
        AuditActions.NodeRestore,
        AuditActions.FileTagsChange,
        AuditActions.ShareGrant,
        AuditActions.ShareChange,
        AuditActions.ShareRevoke,
        AuditActions.ShareLeave,
    ];

    /// <summary>Newest first, <paramref name="limit"/> at a time; pass the last entry's time as <paramref name="before"/> for more.</summary>
    public async Task<NodeActivity> ListAsync(
        Actor actor,
        Guid nodeId,
        DateTimeOffset? before,
        int limit,
        CancellationToken ct = default
    )
    {
        var node = await drive.GetAsync(actor, nodeId, ct);
        var owner = node.OwnerId == actor.UserId;
        limit = Math.Clamp(limit, 1, MaxLimit);

        // A folder's activity takes in everything below it, which anyone who sees the folder sees too (§9).
        var targets =
            node.Kind == NodeKind.Folder
                ? db.Database.SqlQuery<Guid>(
                    $"SELECT id AS \"Value\" FROM nodes WHERE path <@ {node.Path}::ltree"
                )
                : new[] { node.Id }.AsQueryable();
        var query = db
            .AuditEvents.AsNoTracking()
            .Where(e => e.TargetNodeId != null && Shown.Contains(e.Action))
            .Where(e => targets.Contains(e.TargetNodeId!.Value))
            .Where(e =>
                owner || e.Action != AuditActions.FileDownload || e.ActorUserId == actor.UserId
            );
        if (before is { } at)
        {
            query = query.Where(e => e.At < at);
        }

        var events = await query
            .OrderByDescending(e => e.At)
            .ThenByDescending(e => e.Id)
            .Take(limit + 1)
            .ToListAsync(ct);
        var more = events.Count > limit;
        events = events.Take(limit).ToList();

        var userIds = events
            .Select(e => e.ActorUserId)
            .OfType<Guid>()
            .Concat(events.Select(e => GuidAt(e.Details, "user")).OfType<Guid>())
            .Distinct()
            .ToList();
        var users = await db
            .Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);

        // Names come from the nodes as they are now, and only for folders the person can see.
        var nodeIds = events
            .Select(e => e.TargetNodeId!.Value)
            .Concat(events.Select(e => GuidAt(e.Details, "from")).OfType<Guid>())
            .Concat(events.Select(e => GuidAt(e.Details, "to")).OfType<Guid>())
            .Distinct()
            .ToList();
        var nodes = await db
            .Nodes.VisibleTo(actor)
            .AsNoTracking()
            .Where(n => nodeIds.Contains(n.Id))
            .ToDictionaryAsync(n => n.Id, ct);

        var entries = events.Select(e => Entry(e, users, nodes)).ToList();
        return new NodeActivity(entries, more);
    }

    private static ActivityEntry Entry(
        AuditEvent e,
        Dictionary<Guid, User> users,
        Dictionary<Guid, Node> nodes
    )
    {
        var target = e.TargetNodeId!.Value;
        var details = e.Details;
        var place = (string key) =>
            details?.ContainsKey(key) == true
                ? GuidAt(details, key) is { } id
                    ? nodes.TryGetValue(id, out var folder)
                        ? new ActivityPlace(folder.Id, folder.Name.Value)
                        : ActivityPlace.Hidden
                    : ActivityPlace.Top
                : null;
        return e.Action switch
        {
            AuditActions.NodeRename => new ActivityEntry(e, users, nodes, target)
            {
                FromName = StringAt(details, "from"),
                ToName = StringAt(details, "to"),
            },
            AuditActions.NodeMove => new ActivityEntry(e, users, nodes, target)
            {
                From = place("from"),
                To = place("to"),
                Tag = StringAt(details, "tag"),
            },
            AuditActions.FileTagsChange => new ActivityEntry(e, users, nodes, target)
            {
                Added = StringsAt(details, "added"),
                Removed = StringsAt(details, "removed"),
            },
            AuditActions.ShareGrant or AuditActions.ShareChange or AuditActions.ShareRevoke =>
                new ActivityEntry(e, users, nodes, target)
                {
                    Person = GuidAt(details, "user") is { } id ? users.GetValueOrDefault(id) : null,
                    Role = StringAt(details, "role"),
                },
            AuditActions.VersionRestore => new ActivityEntry(e, users, nodes, target)
            {
                VersionNumber = (int?)details?["restoredFrom"],
            },
            _ => new ActivityEntry(e, users, nodes, target),
        };
    }

    private static Guid? GuidAt(JsonObject? details, string key) =>
        details?[key] is JsonValue value
        && value.TryGetValue<string>(out var text)
        && Guid.TryParse(text, out var id)
            ? id
            : null;

    private static string? StringAt(JsonObject? details, string key) =>
        details?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static List<string>? StringsAt(JsonObject? details, string key) =>
        details?[key] is JsonArray items
            ? items.Select(i => i?.GetValue<string>()).OfType<string>().ToList()
            : null;
}

public sealed record NodeActivity(List<ActivityEntry> Entries, bool More);

/// <summary>A folder an entry names: one the person can see, the top of the owner's files, or one they can't see.</summary>
public sealed record ActivityPlace(Guid? Id, string? Name, bool Visible = true)
{
    public static readonly ActivityPlace Top = new(null, null);
    public static readonly ActivityPlace Hidden = new(null, null, Visible: false);
}

/// <summary>One audit event as the Activity tab shows it.</summary>
public sealed record ActivityEntry
{
    public ActivityEntry(
        AuditEvent e,
        Dictionary<Guid, User> users,
        Dictionary<Guid, Node> nodes,
        Guid target
    )
    {
        Id = e.Id;
        At = e.At;
        Action = e.Action;
        Actor = e.ActorUserId is { } id ? users.GetValueOrDefault(id) : null;
        NodeId = target;
        Node = nodes.GetValueOrDefault(target);
    }

    public Guid Id { get; }
    public DateTimeOffset At { get; }
    public string Action { get; }

    /// <summary>Null for the system, such as a filing rule moving a file.</summary>
    public User? Actor { get; }
    public Guid NodeId { get; }

    /// <summary>The node as it is now; null once it's deleted forever.</summary>
    public Node? Node { get; }
    public string? FromName { get; init; }
    public string? ToName { get; init; }
    public ActivityPlace? From { get; init; }
    public ActivityPlace? To { get; init; }
    public string? Tag { get; init; }
    public List<string>? Added { get; init; }
    public List<string>? Removed { get; init; }
    public User? Person { get; init; }
    public string? Role { get; init; }
    public int? VersionNumber { get; init; }
}
