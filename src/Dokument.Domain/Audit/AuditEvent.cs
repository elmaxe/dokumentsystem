using System.Text.Json.Nodes;

namespace Dokument.Domain.Audit;

/// <summary>An append-only record of something a user did (docs/design.md §18), written in the same transaction as the change.</summary>
public sealed class AuditEvent
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public DateTimeOffset At { get; init; }
    public Guid? ActorUserId { get; init; }
    public AuditActorKind ActorKind { get; init; }
    public string? Ip { get; init; }
    public string? UserAgent { get; init; }
    public required string Action { get; init; }
    public Guid? TargetNodeId { get; init; }
    public Guid? TargetVersionId { get; init; }
    public JsonObject? Details { get; init; }
}

public enum AuditActorKind
{
    User,
    Link,
    System,
    ApiKey,
}

/// <summary>The audited file actions. Preview opens are deliberately absent (§18, decision 3).</summary>
public static class AuditActions
{
    public const string FolderCreate = "folder.create";
    public const string FileUpload = "file.upload";
    public const string FileCopy = "file.copy";
    public const string VersionUpload = "version.upload";
    public const string VersionRestore = "version.restore";
    public const string VersionKeepForever = "version.keep_forever";
    public const string NodeRename = "node.rename";
    public const string NodeMove = "node.move";
    public const string FileDownload = "file.download";
}
