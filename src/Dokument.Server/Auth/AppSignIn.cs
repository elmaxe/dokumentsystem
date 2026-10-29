using Dokument.Domain.Identity;
using Dokument.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Dokument.Server.Auth;

/// <summary>
/// What the mobile app needs around the token endpoint (docs/design.md §5, §15.3): the https page the IdP sends its
/// code to, which hands it on to the app, and the proof that lets the app trust a local address.
/// </summary>
public static class AppSignIn
{
    /// <summary>Where the callback hands the IdP's answer to the app. The app registers this scheme.</summary>
    public const string AppReturnUrl = "dokumentsystem://signed-in";

    public static RouteGroupBuilder MapAppSignIn(this RouteGroupBuilder auth)
    {
        auth.MapGet("/app-callback", Callback)
            .WithName("AppSignInCallback")
            .AllowAnonymous()
            .ExcludeFromDescription();
        auth.MapPost("/proof", ProveAsync)
            .WithName("ProveSession")
            .AllowAnonymous()
            .RequireRateLimiting(AuthSetup.AuthRateLimitPolicy)
            .WithDescription(
                "Proves this server issued a bearer session, for the app checking a local address. Returns an "
                    + "HMAC-SHA256 of the nonce keyed by the SHA-256 of the session token. 404 for unknown sessions."
            );
        return auth;
    }

    /// <summary>
    /// The redirect URI the app signs in with: an https page at the public URL, so the IdP only ever needs ordinary
    /// redirect URIs (decision 37). The token endpoint redeems codes for it.
    /// </summary>
    internal static string RedirectUri(HttpRequest request) =>
        $"{request.Scheme}://{request.Host}{request.PathBase}/api/v1/auth/app-callback";

    /// <summary>
    /// Hands the IdP's answer (the code and state, or an error) to the app as it is. The app checks the state and
    /// redeems the code with its PKCE verifier, so the code is useless to anything else that catches the link.
    /// </summary>
    private static RedirectHttpResult Callback(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Redirect(AppReturnUrl + context.Request.QueryString);
    }

    private static async Task<Results<Ok<SessionProof>, NotFound, ValidationProblem>> ProveAsync(
        ProofRequest request,
        SessionStore sessions,
        CancellationToken ct
    )
    {
        if (!ProofNonce.TryParse(request.Nonce, out var nonce))
        {
            return TypedResults.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    ["nonce"] = ["Send 16 to 64 random bytes as base64url."],
                }
            );
        }

        // Only bearer sessions: the web app has no use for proofs.
        return
            await sessions.FindActiveAsync(request.SessionId, ct)
                is { Kind: SessionKind.Bearer } session
            ? TypedResults.Ok(new SessionProof(session.Prove(nonce)))
            : TypedResults.NotFound();
    }
}

/// <param name="Nonce">16 to 64 random bytes as base64url, fresh for each check.</param>
public sealed record ProofRequest(Guid SessionId, string Nonce);

/// <param name="Proof">Base64url HMAC-SHA256 of the nonce's text, keyed by the SHA-256 of the session token.</param>
public sealed record SessionProof(string Proof);
