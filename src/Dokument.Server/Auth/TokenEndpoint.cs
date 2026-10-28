using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dokument.Domain.Identity;
using Dokument.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Dokument.Server.Auth;

/// <summary>
/// <c>POST /api/v1/auth/token</c>, for clients that run the IdP's sign-in themselves: the API reference now, the mobile
/// app in M5 (docs/design.md §5). They send the IdP's code with their PKCE verifier as an OAuth token request. The
/// server redeems the code as the confidential client, provisions the person as at web login and answers with one of
/// our own bearer sessions as the access token. The IdP's tokens are dropped.
/// </summary>
public static class TokenEndpoint
{
    public const string Path = "/api/v1/auth/token";

    public static RouteHandlerBuilder MapTokenEndpoint(this RouteGroupBuilder auth) =>
        auth.MapPost("/token", RedeemAsync)
            .WithName("RedeemCode")
            .AllowAnonymous()
            .RequireRateLimiting(AuthSetup.AuthRateLimitPolicy)
            .WithDescription(
                "An OAuth token request (form: grant_type=authorization_code, code, code_verifier, redirect_uri) "
                    + "for a code from the IdP. Returns a bearer session as access_token."
            );

    /// <summary>
    /// The clients' redirect URIs we redeem codes for, each also registered at the IdP. Add a client's here.
    /// </summary>
    private static IEnumerable<string> AllowedRedirectUris(
        HttpRequest request,
        IHostEnvironment environment
    )
    {
        if (environment.IsDevelopment())
        {
            yield return ApiReference.RedirectUri(request);
        }
    }

    private static async Task<IResult> RedeemAsync(
        HttpContext context,
        OidcSettings oidc,
        IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
        UserProvisioner provisioner,
        SessionStore sessions,
        IHostEnvironment environment,
        ILoggerFactory loggers,
        CancellationToken ct
    )
    {
        var logger = loggers.CreateLogger(typeof(TokenEndpoint));
        context.Response.Headers.CacheControl = "no-store";
        if (!oidc.Enabled)
        {
            return Error("invalid_request", "Sign-in isn't set up.");
        }

        if (!context.Request.HasFormContentType)
        {
            return Error("invalid_request", "Send the token request as a form.");
        }

        var form = await context.Request.ReadFormAsync(ct);
        if (form["grant_type"] != "authorization_code")
        {
            return Error("unsupported_grant_type", "Only authorization_code is supported.");
        }

        var code = form["code"].ToString();
        var verifier = form["code_verifier"].ToString();
        var redirectUri = form["redirect_uri"].ToString();
        if (code.Length == 0 || verifier.Length == 0)
        {
            return Error("invalid_request", "Send code and code_verifier (PKCE is required).");
        }

        if (!AllowedRedirectUris(context.Request, environment).Contains(redirectUri))
        {
            return Error("invalid_grant", "Codes for that redirect_uri aren't redeemed here.");
        }

        var options = oidcOptions.Get(AuthSetup.OidcScheme);
        var idp = await options.ConfigurationManager!.GetConfigurationAsync(ct);
        var tokens = await RedeemAtIdpAsync(options, idp, code, verifier, redirectUri, ct);
        if (tokens?.IdToken is not { } idToken)
        {
            logger.LogWarning("The IdP didn't redeem a code for {RedirectUri}", redirectUri);
            return Error("invalid_grant", "The IdP didn't accept the code.");
        }

        var validated = await new JsonWebTokenHandler().ValidateTokenAsync(
            idToken,
            new()
            {
                ValidIssuer = idp.Issuer,
                ValidAudience = oidc.ClientId,
                IssuerSigningKeys = idp.SigningKeys,
            }
        );
        if (!validated.IsValid)
        {
            logger.LogWarning(validated.Exception, "The IdP's ID token didn't validate");
            return Error("invalid_grant", "The IdP's ID token didn't validate.");
        }

        var identity = validated.ClaimsIdentity;
        await AddUserInfoAsync(identity, options, idp, tokens.AccessToken, ct);
        var principal = new ClaimsPrincipal(identity);
        var result = await provisioner.ProvisionAsync(
            AuthSetup.IdentityFrom(
                principal,
                oidc,
                AuthSetup.ReadQuotaClaim(principal, oidc, logger)
            ),
            oidc.Policy,
            ct
        );
        if (result.User is not { } user)
        {
            logger.LogWarning(
                "Token request refused for {Subject}: {Reason}",
                principal.FindFirstValue("sub"),
                result.Failure
            );
            return Error("access_denied", $"Sign-in refused: {result.Failure}.");
        }

        var (session, token) = await sessions.CreateAsync(
            user.Id,
            SessionKind.Bearer,
            DeviceName.FromUserAgent(context.Request.Headers.UserAgent),
            ct
        );
        logger.LogInformation("User {UserId} got a bearer session as {Role}", user.Id, user.Role);
        return TypedResults.Json(
            new TokenResponse(
                token.Value,
                "Bearer",
                (long)(session.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds
            )
        );
    }

    private static async Task<OpenIdConnectMessage?> RedeemAtIdpAsync(
        OpenIdConnectOptions options,
        OpenIdConnectConfiguration idp,
        string code,
        string verifier,
        string redirectUri,
        CancellationToken ct
    )
    {
        using var response = await options.Backchannel.PostAsync(
            idp.TokenEndpoint,
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["code_verifier"] = verifier,
                    ["redirect_uri"] = redirectUri,
                    ["client_id"] = options.ClientId!,
                    ["client_secret"] = options.ClientSecret ?? "",
                }
            ),
            ct
        );
        return response.IsSuccessStatusCode
            ? new OpenIdConnectMessage(await response.Content.ReadAsStringAsync(ct))
            : null;
    }

    /// <summary>Adds the userinfo claims the ID token lacks, such as groups, as the web login does.</summary>
    private static async Task AddUserInfoAsync(
        ClaimsIdentity identity,
        OpenIdConnectOptions options,
        OpenIdConnectConfiguration idp,
        string? accessToken,
        CancellationToken ct
    )
    {
        if (string.IsNullOrEmpty(idp.UserInfoEndpoint) || string.IsNullOrEmpty(accessToken))
        {
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, idp.UserInfoEndpoint);
        request.Headers.Authorization = new("Bearer", accessToken);
        using var response = await options.Backchannel.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var userInfo = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (
            !userInfo.RootElement.TryGetProperty("sub", out var sub)
            || sub.GetString() != identity.FindFirst("sub")?.Value
        )
        {
            throw new InvalidOperationException("The IdP's userinfo is for someone else.");
        }

        foreach (var property in userInfo.RootElement.EnumerateObject())
        {
            if (identity.HasClaim(c => c.Type == property.Name))
            {
                continue;
            }

            var values =
                property.Value.ValueKind == JsonValueKind.Array
                    ? property.Value.EnumerateArray()
                    : Enumerable.Repeat(property.Value, 1);
            foreach (var value in values)
            {
                identity.AddClaim(
                    new Claim(
                        property.Name,
                        value.ValueKind == JsonValueKind.String
                            ? value.GetString()!
                            : value.GetRawText()
                    )
                );
            }
        }
    }

    private static IResult Error(string error, string description) =>
        TypedResults.Json(
            new TokenError(error, description),
            statusCode: StatusCodes.Status400BadRequest
        );
}

/// <summary>An OAuth token response. The access token is one of our bearer sessions.</summary>
public sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] long ExpiresIn
);

/// <summary>An OAuth error response (RFC 6749 §5.2).</summary>
public sealed record TokenError(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("error_description")] string ErrorDescription
);
