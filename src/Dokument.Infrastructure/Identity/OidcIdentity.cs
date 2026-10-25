using Dokument.Domain.Identity;

namespace Dokument.Infrastructure.Identity;

/// <summary>What the IdP told us about a person at login.</summary>
/// <param name="Quota">What the quota claim said (§17.1), or <see cref="ClaimedQuota.NotRead"/> when it isn't configured.</param>
public sealed record OidcIdentity(
    string Issuer,
    string Subject,
    string? Email,
    string? DisplayName,
    IReadOnlyCollection<string> Groups,
    ClaimedQuota Quota
);
