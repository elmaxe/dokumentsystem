using System.Security.Claims;
using Dokument.Domain.Identity;
using Dokument.Infrastructure.Identity;
using Dokument.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Dokument.Server.Auth;

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth").WithTags("Auth");
        auth.MapGet("/config", GetConfig).WithName("GetAuthConfig").AllowAnonymous();
        auth.MapGet("/login", Login)
            .WithName("Login")
            .AllowAnonymous()
            .RequireRateLimiting(AuthSetup.AuthRateLimitPolicy)
            .WithDescription(
                "Starts the OIDC login in the browser. Navigate here; don't fetch it."
            );
        auth.MapGet("/session", GetSession).WithName("GetSession").RequireAuthorization();
        auth.MapPost("/logout", Logout).WithName("Logout").RequireAuthorization();

        api.MapPut("/me/language", SetLanguage)
            .WithName("SetMyLanguage")
            .WithTags("Auth")
            .RequireAuthorization()
            .WithDescription("Null follows the browser's language.");

        var sessions = api.MapGroup("/sessions").WithTags("Sessions").RequireAuthorization();
        sessions.MapGet("", ListMySessions).WithName("ListSessions");
        sessions.MapDelete("/{id:guid}", RevokeMySession).WithName("RevokeSession");

        var admin = api.MapGroup("/admin")
            .WithTags("Admin")
            .RequireAuthorization(SessionAuthentication.AdminPolicy);
        admin
            .MapGet("/users/{userId:guid}/sessions", ListUserSessions)
            .WithName("AdminListUserSessions");
        admin.MapDelete("/sessions/{id:guid}", AdminRevokeSession).WithName("AdminRevokeSession");
        return api;
    }

    private static Ok<AuthConfig> GetConfig(OidcSettings oidc) =>
        TypedResults.Ok(new AuthConfig(oidc.Enabled, oidc.ProviderName));

    private static IResult Login(string? returnTo, OidcSettings oidc) =>
        oidc.Enabled
            ? TypedResults.Challenge(
                new AuthenticationProperties { RedirectUri = ReturnUrl.Sanitize(returnTo) },
                [AuthSetup.OidcScheme]
            )
            : TypedResults.Problem(
                title: "Sign-in isn't set up",
                detail: "Set OIDC_ISSUER, OIDC_CLIENT_ID and OIDC_CLIENT_SECRET for the server, then restart it.",
                statusCode: StatusCodes.Status503ServiceUnavailable
            );

    private static async Task<Results<Ok<CurrentSession>, UnauthorizedHttpResult>> GetSession(
        HttpContext context,
        ClaimsPrincipal principal,
        DokumentDbContext db,
        CancellationToken ct
    )
    {
        var user = await db
            .Users.AsNoTracking()
            .SingleOrDefaultAsync(u => u.Id == principal.UserId(), ct);
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        var csrfToken =
            principal.IsCookieSession()
            && context.Items[SessionAuthentication.TokenItem] is SessionToken token
                ? token.CsrfToken()
                : null;
        return TypedResults.Ok(
            new CurrentSession(UserProfile.From(user), principal.SessionId(), csrfToken)
        );
    }

    private static async Task<Results<Ok<UserProfile>, ValidationProblem>> SetLanguage(
        SetLanguageRequest request,
        ClaimsPrincipal principal,
        DokumentDbContext db,
        CancellationToken ct
    )
    {
        UiLanguage? language = null;
        if (request.Language is not null)
        {
            language = UiLanguage.TryParse(request.Language);
            if (language is null)
            {
                return TypedResults.ValidationProblem(
                    new Dictionary<string, string[]>
                    {
                        ["language"] =
                        [
                            $"Pick one of {string.Join(", ", UiLanguage.All.Select(l => l.Code))}.",
                        ],
                    }
                );
            }
        }

        var user = await db.Users.SingleAsync(u => u.Id == principal.UserId(), ct);
        user.Locale = language;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(UserProfile.From(user));
    }

    private static async Task<NoContent> Logout(
        HttpContext context,
        ClaimsPrincipal principal,
        SessionStore sessions,
        CancellationToken ct
    )
    {
        await sessions.RevokeAsync(principal.SessionId(), principal.UserId(), ct);
        SessionAuthentication.DeleteCookie(context);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<List<SessionInfo>>> ListMySessions(
        ClaimsPrincipal principal,
        SessionStore sessions,
        CancellationToken ct
    )
    {
        var current = principal.SessionId();
        var list = await sessions.ListActiveAsync(principal.UserId(), ct);
        return TypedResults.Ok(list.Select(s => SessionInfo.From(s, current)).ToList());
    }

    private static async Task<Results<NoContent, NotFound>> RevokeMySession(
        Guid id,
        ClaimsPrincipal principal,
        SessionStore sessions,
        CancellationToken ct
    ) =>
        await sessions.RevokeAsync(id, principal.UserId(), ct)
            ? TypedResults.NoContent()
            : TypedResults.NotFound();

    private static async Task<Ok<List<SessionInfo>>> ListUserSessions(
        Guid userId,
        ClaimsPrincipal principal,
        SessionStore sessions,
        CancellationToken ct
    )
    {
        var current = principal.SessionId();
        var list = await sessions.ListActiveAsync(userId, ct);
        return TypedResults.Ok(list.Select(s => SessionInfo.From(s, current)).ToList());
    }

    private static async Task<Results<NoContent, NotFound>> AdminRevokeSession(
        Guid id,
        SessionStore sessions,
        CancellationToken ct
    ) =>
        await sessions.RevokeAsync(id, userId: null, ct)
            ? TypedResults.NoContent()
            : TypedResults.NotFound();
}

/// <summary>What the sign-in page needs to know before anyone is signed in.</summary>
public sealed record AuthConfig(bool Enabled, string? ProviderName);

/// <param name="CsrfToken">Send this in <c>X-CSRF-Token</c> on mutations. Null for bearer sessions, which don't need it.</param>
public sealed record CurrentSession(UserProfile User, Guid SessionId, string? CsrfToken);

/// <param name="Language">The UI language the user picked; null follows the browser.</param>
public sealed record UserProfile(
    Guid Id,
    string DisplayName,
    string? Email,
    UserRole Role,
    string? Language
)
{
    public static UserProfile From(User user) =>
        new(user.Id, user.DisplayName, user.Email, user.Role, user.Locale?.Code);
}

/// <param name="Language"><c>sv</c>, <c>en</c>, or null to follow the browser.</param>
public sealed record SetLanguageRequest(string? Language);

public sealed record SessionInfo(
    Guid Id,
    SessionKind Kind,
    string? DeviceName,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    bool Current
)
{
    public static SessionInfo From(Session session, Guid currentSessionId) =>
        new(
            session.Id,
            session.Kind,
            session.DeviceName?.Value,
            session.CreatedAt,
            session.LastSeenAt,
            session.Id == currentSessionId
        );
}
