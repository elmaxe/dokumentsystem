using Dokument.Domain.Identity;

namespace Dokument.Infrastructure.Identity;

/// <summary>What the IdP told us about a person at login.</summary>
/// <param name="Picture">The picture claim (§5.1) when it is a URL we can fetch, otherwise null.</param>
/// <param name="Quota">What the quota claim said (§17.1), or <see cref="ClaimedQuota.NotRead"/> when it isn't configured.</param>
public sealed record OidcIdentity(
    string Issuer,
    string Subject,
    string? Email,
    string? DisplayName,
    IReadOnlyCollection<string> Groups,
    ClaimedQuota Quota,
    string? Picture = null
);
