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

    /// <summary>SHA-256 of the token, which is how a request finds its link.</summary>
    public required byte[] TokenHash { get; init; }

    /// <summary>The token encrypted with the server's Data Protection keys, so the owner can copy the link again.</summary>
    public required string ProtectedToken { get; init; }

    public required bool AllowDownload { get; set; }

    /// <summary>An Argon2id hash in PHC form, or null when the link has no password.</summary>
    public string? PasswordHash { get; set; }

    /// <summary>When it stops working; null for never.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
    public required Guid CreatedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; set; }

    public bool WorksAt(DateTimeOffset now) =>
        RevokedAt is null && (ExpiresAt is not { } end || now < end);
}
