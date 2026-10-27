using Dokument.Domain.Identity;
using Dokument.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dokument.Server.Auth;

/// <summary>
/// Development only: accepts the IdP's own access tokens as bearer tokens, so the API reference (Scalar) can sign in
/// with OAuth and try the API (docs/design.md §5). The token must be meant for us (its audience is our client id).
/// The person is provisioned as at login, but no session is created, so the request has no session id.
/// </summary>
public static class AccessTokenAuthentication
{
    public const string Scheme = "AccessToken";

    /// <summary>Sends JWTs to <see cref="Scheme"/> and everything else to our own sessions.</summary>
    public const string SessionOrAccessTokenScheme = "SessionOrAccessToken";

    public static bool IsEnabled(OidcSettings oidc, IHostEnvironment environment) =>
        environment.IsDevelopment() && oidc.Enabled && oidc.ScalarClientId is not null;

    public static AuthenticationBuilder AddAccessTokens(
        this AuthenticationBuilder auth,
        OidcSettings oidc
    ) =>
        auth.AddPolicyScheme(
                SessionOrAccessTokenScheme,
                displayName: null,
                options =>
                    options.ForwardDefaultSelector = context =>
                        IsJwt(context.Request) ? Scheme : SessionAuthentication.Scheme
            )
            .AddJwtBearer(
                Scheme,
                options =>
                {
                    options.Authority = oidc.Issuer;
                    options.Audience = oidc.ClientId;
                    options.RequireHttpsMetadata = false;
                    options.MapInboundClaims = false;
                    options.Events = new JwtBearerEvents { OnTokenValidated = ProvisionAsync };
                }
            );

    /// <summary>Our session tokens are base64url, so a bearer token with dots is a JWT.</summary>
    private static bool IsJwt(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && authorization.Contains('.', StringComparison.Ordinal);
    }

    private static async Task ProvisionAsync(TokenValidatedContext context)
    {
        var http = context.HttpContext;
        var oidc = http.RequestServices.GetRequiredService<OidcSettings>();

        // The quota claim is left alone: only a real login reads it (§17.1).
        var identity = AuthSetup.IdentityFrom(context.Principal!, oidc, ClaimedQuota.NotRead);
        var result = await http
            .RequestServices.GetRequiredService<UserProvisioner>()
            .ProvisionAsync(identity, oidc.Policy, http.RequestAborted);
        if (result.User is not { } user)
        {
            context.Fail($"Sign-in refused: {result.Failure}");
            return;
        }

        context.Principal = SessionAuthentication.CreatePrincipal(
            user,
            sessionId: Guid.Empty,
            sessionKind: Scheme,
            Scheme
        );
    }
}
