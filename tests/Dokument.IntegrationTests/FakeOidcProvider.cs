using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Dokument.IntegrationTests;

/// <summary>A test person at the fake IdP.</summary>
public sealed record FakeUser(string Subject, string Name, string Email, params string[] Groups)
{
    /// <summary>More claims for userinfo, such as a quota claim. Values are serialized as given, so numbers stay numbers.</summary>
    public IReadOnlyDictionary<string, object> Claims { get; init; } =
        new Dictionary<string, object>();
}

/// <summary>
/// Just enough of an OIDC provider for the code + PKCE flow: discovery, JWKS, authorize (signs in <see cref="NextUser"/>
/// at once), token and userinfo. It runs in memory; the server reaches it through <see cref="Handler"/>.
/// </summary>
public sealed class FakeOidcProvider : IAsyncDisposable
{
    public const string Issuer = "https://idp.test";
    public const string ClientId = "dokument";
    public const string ClientSecret = "test-secret";

    private readonly RsaSecurityKey key = new(RSA.Create(2048)) { KeyId = "test-key" };
    private readonly ConcurrentDictionary<
        string,
        (FakeUser User, string Nonce, string Challenge, string RedirectUri)
    > codes = new();
    private readonly ConcurrentDictionary<string, FakeUser> accessTokens = new();
    private readonly WebApplication app;

    public FakeUser NextUser { get; set; } =
        new("alice-sub", "Alice Andersson", "alice@example.com");

    private FakeOidcProvider(WebApplication app) => this.app = app;

    public HttpMessageHandler Handler => app.GetTestServer().CreateHandler();

    public HttpClient CreateBrowser() => new(Handler) { BaseAddress = new Uri(Issuer) };

    /// <summary>A signed JWT access token for <paramref name="user"/>, as an IdP issues to the API reference's client.</summary>
    public string CreateAccessToken(FakeUser user, string audience = ClientId)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(
            new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Audience = audience,
                IssuedAt = now,
                NotBefore = now,
                Expires = now.AddMinutes(5),
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
                Claims = new Dictionary<string, object>
                {
                    ["sub"] = user.Subject,
                    ["azp"] = "dokument-scalar",
                    ["name"] = user.Name,
                    ["email"] = user.Email,
                    ["groups"] = user.Groups,
                },
            }
        );
    }

    public static async Task<FakeOidcProvider> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var provider = new FakeOidcProvider(builder.Build());
        provider.Map();
        await provider.app.StartAsync();
        return provider;
    }

    private void Map()
    {
        app.MapGet(
            "/.well-known/openid-configuration",
            () =>
                Results.Json(
                    new Dictionary<string, object>
                    {
                        ["issuer"] = Issuer,
                        ["authorization_endpoint"] = $"{Issuer}/authorize",
                        ["token_endpoint"] = $"{Issuer}/token",
                        ["userinfo_endpoint"] = $"{Issuer}/userinfo",
                        ["jwks_uri"] = $"{Issuer}/jwks",
                        ["response_types_supported"] = new[] { "code" },
                        ["subject_types_supported"] = new[] { "public" },
                        ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
                        ["code_challenge_methods_supported"] = new[] { "S256" },
                    }
                )
        );

        app.MapGet(
            "/jwks",
            () =>
            {
                var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(
                    new RsaSecurityKey(key.Rsa.ExportParameters(false)) { KeyId = key.KeyId }
                );
                return Results.Json(
                    new
                    {
                        keys = new[]
                        {
                            new
                            {
                                kty = jwk.Kty,
                                kid = jwk.Kid,
                                use = "sig",
                                alg = "RS256",
                                n = jwk.N,
                                e = jwk.E,
                            },
                        },
                    }
                );
            }
        );

        app.MapGet(
            "/authorize",
            (HttpRequest request) =>
            {
                var q = request.Query;
                if (
                    q["client_id"] != ClientId
                    || q["response_type"] != "code"
                    || q["code_challenge_method"] != "S256"
                )
                {
                    return Results.BadRequest("bad authorize request");
                }

                var code = Guid.NewGuid().ToString("N");
                codes[code] = (
                    NextUser,
                    q["nonce"].ToString(),
                    q["code_challenge"].ToString(),
                    q["redirect_uri"].ToString()
                );
                return Results.Redirect(
                    $"{q["redirect_uri"]}?code={code}&state={Uri.EscapeDataString(q["state"].ToString())}"
                );
            }
        );

        app.MapPost(
            "/token",
            async (HttpRequest request) =>
            {
                var form = await request.ReadFormAsync();
                if (
                    form["client_id"] != ClientId
                    || form["client_secret"] != ClientSecret
                    || !codes.TryRemove(form["code"].ToString(), out var grant)
                    || form["redirect_uri"] != grant.RedirectUri
                )
                {
                    return Results.BadRequest(new { error = "invalid_grant" });
                }

                var challenge = Base64UrlEncoder.Encode(
                    SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString()))
                );
                if (challenge != grant.Challenge)
                {
                    return Results.BadRequest(
                        new { error = "invalid_grant", error_description = "PKCE mismatch" }
                    );
                }

                var accessToken = Guid.NewGuid().ToString("N");
                accessTokens[accessToken] = grant.User;
                var now = DateTime.UtcNow;
                var idToken = new JsonWebTokenHandler().CreateToken(
                    new SecurityTokenDescriptor
                    {
                        Issuer = Issuer,
                        Audience = ClientId,
                        IssuedAt = now,
                        NotBefore = now,
                        Expires = now.AddMinutes(5),
                        SigningCredentials = new SigningCredentials(
                            key,
                            SecurityAlgorithms.RsaSha256
                        ),
                        Claims = new Dictionary<string, object>
                        {
                            ["sub"] = grant.User.Subject,
                            ["nonce"] = grant.Nonce,
                            ["name"] = grant.User.Name,
                            ["email"] = grant.User.Email,
                        },
                    }
                );
                return Results.Json(
                    new
                    {
                        access_token = accessToken,
                        token_type = "Bearer",
                        expires_in = 300,
                        id_token = idToken,
                    }
                );
            }
        );

        // Groups come from userinfo only, as with IdPs that keep the ID token small.
        app.MapGet(
            "/userinfo",
            (HttpRequest request) =>
                accessTokens.TryGetValue(
                    request
                        .Headers.Authorization.ToString()
                        .Replace("Bearer ", "", StringComparison.Ordinal),
                    out var user
                )
                    ? Results.Json(
                        new Dictionary<string, object>(user.Claims)
                        {
                            ["sub"] = user.Subject,
                            ["name"] = user.Name,
                            ["email"] = user.Email,
                            ["groups"] = user.Groups,
                        }
                    )
                    : Results.Unauthorized()
        );
    }

    public async ValueTask DisposeAsync() => await app.DisposeAsync();
}
