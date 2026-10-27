using System.Security.Claims;
using System.Text.Encodings.Web;
using Dokument.Domain.Identity;
using Dokument.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dokument.Server.Auth;

/// <summary>Claim types and names shared by the session scheme and the endpoints.</summary>
public static class SessionAuthentication
{
    public const string Scheme = "Session";
    public const string CookieName = "dokument_session";
    public const string CsrfHeader = "X-CSRF-Token";
    public const string AdminPolicy = "Admin";

    public const string UserIdClaim = "uid";
    public const string SessionIdClaim = "sid";
    public const string SessionKindClaim = "session_kind";
    public const string RoleClaim = "role";

    /// <summary>The raw token of the current request, kept for the CSRF check.</summary>
    internal const string TokenItem = "dokument.session.token";

    public static Guid UserId(this ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(UserIdClaim)!);

    public static Guid SessionId(this ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(SessionIdClaim)!);

    public static bool IsCookieSession(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(SessionKindClaim) == nameof(SessionKind.Cookie);

    public static CookieOptions CookieOptions(HttpRequest request, DateTimeOffset? expires) =>
        new()
        {
            HttpOnly = true,
            Secure = request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = expires,
            IsEssential = true,
        };

    public static void SetCookie(HttpContext context, SessionToken token, DateTimeOffset expires) =>
        context.Response.Cookies.Append(
            CookieName,
            token.Value,
            CookieOptions(context.Request, expires)
        );

    /// <param name="sessionId">The session behind the request; <see cref="Guid.Empty"/> when there is none.</param>
    internal static ClaimsPrincipal CreatePrincipal(
        User user,
        Guid sessionId,
        string sessionKind,
        string scheme
    ) =>
        new(
            new ClaimsIdentity(
                [
                    new Claim(UserIdClaim, user.Id.ToString()),
                    new Claim(SessionIdClaim, sessionId.ToString()),
                    new Claim(SessionKindClaim, sessionKind),
                    new Claim(RoleClaim, user.Role.ToString().ToLowerInvariant()),
                    new Claim(ClaimTypes.Name, user.DisplayName),
                ],
                scheme,
                nameType: ClaimTypes.Name,
                roleType: RoleClaim
            )
        );

    public static void DeleteCookie(HttpContext context) =>
        context.Response.Cookies.Delete(CookieName, CookieOptions(context.Request, expires: null));
}

/// <summary>
/// Authenticates a request from the session cookie (web) or an <c>Authorization: Bearer</c> token (mobile).
/// Both are opaque tokens looked up in <c>sessions</c>, so revoking a session takes effect on the next request.
/// </summary>
public sealed class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SessionStore sessions
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var (raw, fromCookie) = ReadToken();
        if (raw is null)
        {
            return AuthenticateResult.NoResult();
        }

        if (!SessionToken.TryParse(raw, out var token))
        {
            if (fromCookie && !Response.HasStarted)
            {
                SessionAuthentication.DeleteCookie(Context);
            }

            return AuthenticateResult.Fail("The session token is malformed.");
        }

        var (session, refreshed) = await sessions.ValidateAsync(token, Context.RequestAborted);
        if (
            session?.User is not { } user
            || session.Kind != (fromCookie ? SessionKind.Cookie : SessionKind.Bearer)
        )
        {
            if (fromCookie && !Response.HasStarted)
            {
                SessionAuthentication.DeleteCookie(Context);
            }

            return AuthenticateResult.Fail("The session has expired or was revoked.");
        }

        if (fromCookie && refreshed && !Response.HasStarted)
        {
            // Keep the browser cookie's expiry in step with the sliding session.
            SessionAuthentication.SetCookie(Context, token, session.ExpiresAt);
        }

        Context.Items[SessionAuthentication.TokenItem] = token;
        return AuthenticateResult.Success(
            new AuthenticationTicket(
                SessionAuthentication.CreatePrincipal(
                    user,
                    session.Id,
                    session.Kind.ToString(),
                    SessionAuthentication.Scheme
                ),
                SessionAuthentication.Scheme
            )
        );
    }

    private (string? Token, bool FromCookie) ReadToken()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var bearer = authorization["Bearer ".Length..].Trim();
            return (bearer.Length > 0 ? bearer : null, false);
        }

        return
            Request.Cookies.TryGetValue(SessionAuthentication.CookieName, out var cookie)
            && cookie.Length > 0
            ? (cookie, true)
            : (null, false);
    }
}
