using System.Security.Claims;
using Dokument.Infrastructure.Identity;
using Dokument.Infrastructure.Live;
using Dokument.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dokument.Server.Auth;

/// <summary>
/// Signing out (docs/design.md §5): ends our session, and gives the client the IdP's end-session URL so the person is
/// signed out at the IdP too (OIDC RP-initiated logout). The IdP sends the browser back to one of the pages here.
/// </summary>
public static class SignOut
{
    /// <summary>Where the app's sign-out bounce hands back to the app. The app registers this scheme.</summary>
    public const string AppReturnUrl = "dokumentsystem://signed-out";

    public static RouteGroupBuilder MapSignOut(this RouteGroupBuilder auth)
    {
        auth.MapPost("/logout", LogoutAsync)
            .WithName("Logout")
            .RequireAuthorization()
            .WithDescription(
                "Ends this session. Then send the browser to endSessionUrl, when there is one, to sign out at the "
                    + "IdP as well; the IdP returns to the sign-in page (web) or to the app."
            );
        auth.MapGet("/signed-out", SignedOut)
            .WithName("SignedOut")
            .AllowAnonymous()
            .ExcludeFromDescription();
        auth.MapGet("/app-signed-out", AppSignedOut)
            .WithName("AppSignedOut")
            .AllowAnonymous()
            .ExcludeFromDescription();
        return auth;
    }

    /// <summary>The post-logout redirect URI for web sessions. Register it at the IdP.</summary>
    internal static string WebReturnUri(HttpRequest request) =>
        $"{request.Scheme}://{request.Host}{request.PathBase}/api/v1/auth/signed-out";

    /// <summary>The post-logout redirect URI for the app's bearer sessions. Register it at the IdP.</summary>
    internal static string AppReturnUri(HttpRequest request) =>
        $"{request.Scheme}://{request.Host}{request.PathBase}/api/v1/auth/app-signed-out";

    private static async Task<Ok<SignOutResult>> LogoutAsync(
        HttpContext context,
        ClaimsPrincipal principal,
        SessionStore sessions,
        DokumentDbContext db,
        OidcSettings oidc,
        IServiceProvider services,
        ILoggerFactory loggers,
        CancellationToken ct
    )
    {
        var sessionId = principal.SessionId();
        var idToken = await db
            .Sessions.Where(s => s.Id == sessionId)
            .Select(s => s.IdToken)
            .SingleOrDefaultAsync(ct);
        await sessions.RevokeAsync(sessionId, principal.UserId(), SessionEndReason.SignedOut, ct);
        SessionAuthentication.DeleteCookie(context);

        var returnUri = principal.IsCookieSession()
            ? WebReturnUri(context.Request)
            : AppReturnUri(context.Request);
        var url = oidc.Enabled
            ? await EndSessionUrlAsync(services, oidc, idToken, returnUri, loggers, ct)
            : null;
        return TypedResults.Ok(new SignOutResult(url));
    }

    /// <summary>
    /// The IdP's end-session URL with the ID token as hint, so the IdP signs the person out without asking and returns
    /// to <paramref name="returnUri"/>. Null when the IdP has no end-session endpoint or can't be reached: then signing
    /// out stays local.
    /// </summary>
    private static async Task<string?> EndSessionUrlAsync(
        IServiceProvider services,
        OidcSettings oidc,
        string? idToken,
        string returnUri,
        ILoggerFactory loggers,
        CancellationToken ct
    )
    {
        string? endpoint;
        try
        {
            var options = services
                .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
                .Get(AuthSetup.OidcScheme);
            endpoint = (
                await options.ConfigurationManager!.GetConfigurationAsync(ct)
            ).EndSessionEndpoint;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            loggers
                .CreateLogger(typeof(SignOut))
                .LogWarning(
                    e,
                    "Couldn't read the IdP's end-session endpoint; signed out here only"
                );
            return null;
        }

        if (string.IsNullOrEmpty(endpoint))
        {
            return null;
        }

        var query = new Dictionary<string, string?>
        {
            ["client_id"] = oidc.ClientId,
            ["post_logout_redirect_uri"] = returnUri,
        };
        if (idToken is not null)
        {
            query["id_token_hint"] = idToken;
        }

        return QueryHelpers.AddQueryString(endpoint, query);
    }

    private static RedirectHttpResult SignedOut(HttpRequest request) =>
        TypedResults.Redirect($"{request.PathBase}/signin?reason=signedOut");

    private static RedirectHttpResult AppSignedOut(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Redirect(AppReturnUrl);
    }
}

/// <param name="EndSessionUrl">
/// Send the browser here to sign out at the IdP too. Null when the IdP has no end-session endpoint: you're signed out
/// here only.
/// </param>
public sealed record SignOutResult(string? EndSessionUrl);
