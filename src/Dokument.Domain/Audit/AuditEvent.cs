using System.Text.Json.Nodes;

namespace Dokument.Domain.Audit;

/// <summary>An append-only record of something a user or the system did (docs/design.md §18), written in the same transaction as the change.</summary>
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

/// <summary>The audited actions. Preview opens are deliberately absent (§18, decision 3).</summary>
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
    public const string NodeTrash = "node.trash";
    public const string NodeRestore = "node.restore";
    public const string NodeDeleteForever = "node.delete_forever";
    public const string TrashEmpty = "trash.empty";

    /// <summary>Tags added to or removed from a file (§13.4), naming the tags.</summary>
    public const string FileTagsChange = "file.tags_change";

    /// <summary>The purge job removed trash past its retention (§12). Written by the system.</summary>
    public const string NodePurge = "node.purge";

    /// <summary>The purge job removed a version past the retention policy (§8.2). Written by the system.</summary>
    public const string VersionPurge = "version.purge";
    public const string SettingsChange = "settings.change";
    public const string QuotaChange = "user.quota_change";

    /// <summary>An admin added, changed or deleted a keyword rule (§13.4).</summary>
    public const string TagRuleChange = "admin.tag_rule";
}
