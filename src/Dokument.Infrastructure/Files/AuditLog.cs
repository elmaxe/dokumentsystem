using System.Text.Json.Nodes;
using Dokument.Application.Files;
using Dokument.Domain.Audit;
using Dokument.Infrastructure.Persistence;

namespace Dokument.Infrastructure.Files;

/// <summary>Adds audit events to the current unit of work, so they commit with the change they describe (docs/design.md §18).</summary>
public sealed class AuditLog(DokumentDbContext db, TimeProvider time)
{
    public void Add(
        Actor actor,
        string action,
        Guid? nodeId = null,
        Guid? versionId = null,
        JsonObject? details = null
    ) =>
        db.AuditEvents.Add(
            new AuditEvent
            {
                At = time.GetUtcNow(),
                ActorUserId = actor.UserId,
                ActorKind = AuditActorKind.User,
                Ip = actor.Ip,
                UserAgent = actor.UserAgent is { Length: > 512 } ua ? ua[..512] : actor.UserAgent,
                Action = action,
                TargetNodeId = nodeId,
                TargetVersionId = versionId,
                Details = details,
            }
        );

    /// <summary>Something the system did on its own, such as the purge job (§12).</summary>
    public void AddSystem(
        string action,
        Guid? nodeId = null,
        Guid? versionId = null,
        JsonObject? details = null
    ) =>
        db.AuditEvents.Add(
            new AuditEvent
            {
                At = time.GetUtcNow(),
                ActorKind = AuditActorKind.System,
                Action = action,
                TargetNodeId = nodeId,
                TargetVersionId = versionId,
                Details = details,
            }
        );
}
