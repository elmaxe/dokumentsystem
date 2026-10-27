using Dokument.Application.Identity;
using Microsoft.Extensions.Configuration;

namespace Dokument.Server.Auth;

/// <summary>
/// OIDC bootstrap configuration from environment variables (docs/design.md §4.3, §5). Sign-in is off until
/// <c>OIDC_ISSUER</c> and <c>OIDC_CLIENT_ID</c> are set, so a fresh install still starts and passes health checks.
/// </summary>
/// <param name="QuotaClaim">The claim that sets each person's quota at login (§17.1); null reads none.</param>
/// <param name="ScalarClientId">
/// Development only: the IdP client the API reference (Scalar) signs in with. Setting it makes the server accept that
/// client's access tokens (<see cref="AccessTokenAuthentication"/>).
/// </param>
public sealed record OidcSettings(
    string? Issuer,
    string? ClientId,
    string? ClientSecret,
    string Scope,
    string GroupsClaim,
    string? ProviderName,
    LoginPolicy Policy,
    string? QuotaClaim = null,
    string? ScalarClientId = null
)
{
    public bool Enabled =>
        !string.IsNullOrWhiteSpace(Issuer) && !string.IsNullOrWhiteSpace(ClientId);

    public static OidcSettings FromConfiguration(IConfiguration configuration)
    {
        string? Get(string key) =>
            string.IsNullOrWhiteSpace(configuration[key]) ? null : configuration[key]!.Trim();

        return new OidcSettings(
            Issuer: Get("OIDC_ISSUER"),
            ClientId: Get("OIDC_CLIENT_ID"),
            ClientSecret: Get("OIDC_CLIENT_SECRET"),
            Scope: Get("OIDC_SCOPE") ?? "openid profile email",
            GroupsClaim: Get("OIDC_GROUPS_CLAIM") ?? "groups",
            ProviderName: Get("OIDC_PROVIDER_NAME"),
            Policy: new LoginPolicy(
                UserGroup: Get("OIDC_USER_GROUP"),
                AdminGroup: Get("OIDC_ADMIN_GROUP") ?? LoginPolicy.DefaultAdminGroup
            ),
            QuotaClaim: Get("OIDC_QUOTA_CLAIM"),
            ScalarClientId: Get("OIDC_SCALAR_CLIENT_ID")
        );
    }
}
