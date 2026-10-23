namespace Dokument.Infrastructure.Identity;

/// <summary>What the IdP told us about a person at login.</summary>
public sealed record OidcIdentity(
    string Issuer,
    string Subject,
    string? Email,
    string? DisplayName,
    IReadOnlyCollection<string> Groups
);
