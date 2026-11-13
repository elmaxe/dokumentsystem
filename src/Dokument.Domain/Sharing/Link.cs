namespace Dokument.Domain.Sharing;

/// <summary>
/// A public link to a file or folder (docs/design.md §9): anyone who has it can look without signing in. A link only
/// ever lets people view; a folder's link covers what is in it now and what is added later. A node has at most one
/// working link, which its owner turns on, changes and turns off.
/// </summary>
public sealed class Link
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required Guid NodeId { get; init; }

    /// <summary>The secret in the link's address, stored as it is so the owner can copy the link again.</summary>
    public required LinkToken Token { get; init; }

    public required bool AllowDownload { get; set; }

    /// <summary>Null when the link has no password.</summary>
    public PasswordHash? PasswordHash { get; set; }

    /// <summary>When it stops working; null for never.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
    public required Guid CreatedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; set; }

    public bool WorksAt(DateTimeOffset now) =>
        RevokedAt is null && (ExpiresAt is not { } end || now < end);
}
