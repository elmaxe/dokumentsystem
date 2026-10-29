namespace Dokument.Domain.Sharing;

/// <summary>
/// A file or folder shared with one person (docs/design.md §9). A share on a folder covers everything inside it, now
/// and later; a share lower down can only add to what the person already has.
/// </summary>
public sealed class Share
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required Guid NodeId { get; init; }
    public required Guid GranteeUserId { get; init; }
    public required ShareRole Role { get; set; }
    public required Guid CreatedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the person first opened it from Shared with me. Null shows it as New.</summary>
    public DateTimeOffset? SeenAt { get; set; }
}

/// <summary>What a share lets someone do. The numbers order the roles: a higher one includes the lower.</summary>
public enum ShareRole
{
    /// <summary>Open, preview, search, download and star.</summary>
    Viewer = 1,

    /// <summary>Also upload, make folders, rename, move inside the owner's folders, trash, tag and edit details.</summary>
    Editor = 2,
}
