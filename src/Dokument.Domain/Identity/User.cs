namespace Dokument.Domain.Identity;

/// <summary>A person, created on first OIDC login and identified by issuer + subject (docs/design.md §5, §6).</summary>
public sealed class User
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required string OidcIssuer { get; init; }
    public required string OidcSubject { get; init; }
    public string? Email { get; set; }
    public required string DisplayName { get; set; }
    public UserRole Role { get; set; }

    /// <summary>Null means the admin-set default quota applies.</summary>
    public long? QuotaBytes { get; set; }
    public long UsedBytes { get; set; }
    public long ReservedBytes { get; set; }
    public string? Locale { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }

    public bool IsDisabled => DisabledAt is not null;
}

public enum UserRole
{
    User,
    Admin,
}
