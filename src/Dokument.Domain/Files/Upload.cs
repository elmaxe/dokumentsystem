namespace Dokument.Domain.Files;

/// <summary>
/// A resumable tus upload in progress (docs/design.md §8.1). Bytes go to <c>tmp/{Id}</c> on the originals volume and
/// the quota is reserved until it completes, is cancelled or expires.
/// </summary>
public sealed class Upload
{
    /// <summary>Abandoned uploads are cleaned up after this long without progress.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required Guid UserId { get; init; }

    /// <summary>Whose quota the reservation is held against: the owner of the folder the file goes in (§9).</summary>
    public required Guid OwnerId { get; init; }

    /// <summary>The folder the new file goes in; null for the root. Ignored when <see cref="TargetNodeId"/> is set.</summary>
    public Guid? ParentId { get; init; }

    /// <summary>Set for "upload new version": the existing file this upload becomes a version of.</summary>
    public Guid? TargetNodeId { get; init; }
    public required NodeName Name { get; init; }
    public required long Size { get; init; }
    public long Offset { get; set; }

    /// <summary>The hash the client computed before uploading, checked on completion.</summary>
    public ContentHash? ClaimedHash { get; init; }
    public string? MimeType { get; init; }
    public DateTimeOffset? ClientModifiedAt { get; init; }
    public NameConflictPolicy OnConflict { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>The file the upload produced, once complete.</summary>
    public Guid? ResultNodeId { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public bool IsComplete => CompletedAt is not null;
    public bool AllBytesReceived => Offset == Size;
}

/// <summary>What happens when a new file's name is already taken in its folder (docs/design.md §6).</summary>
public enum NameConflictPolicy
{
    /// <summary>Refuse, so the client can ask the user.</summary>
    Fail,

    /// <summary>Keep both: the new file becomes "name (1).ext".</summary>
    KeepBoth,

    /// <summary>Upload as a new version of the existing file.</summary>
    NewVersion,
}

/// <summary>What happens when a folder upload's top folder has the name of something already there (docs/design.md §8.1).</summary>
public enum FolderConflictPolicy
{
    /// <summary>Refuse, so the client can ask the user.</summary>
    Fail,

    /// <summary>Add to the existing folder, reusing subfolders with the same names.</summary>
    Merge,

    /// <summary>Keep both: the new folder becomes "name (1)".</summary>
    KeepBoth,
}
