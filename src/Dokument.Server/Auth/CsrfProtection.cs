using Dokument.Domain.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Dokument.Server.Auth;

/// <summary>
/// Requires the session's CSRF token in <c>X-CSRF-Token</c> on mutations made with the session cookie (docs/design.md §5).
/// Bearer tokens aren't sent automatically by browsers, so they need no CSRF check.
/// </summary>
public static class CsrfProtection
{
    public static IApplicationBuilder UseCsrfProtection(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var request = context.Request;
        var user = context.User;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method) &&
            user.Identity?.IsAuthenticated == true && user.IsCookieSession() &&
            context.Items[SessionAuthentication.TokenItem] is SessionToken token &&
            !token.CsrfTokenMatches(request.Headers[SessionAuthentication.CsrfHeader]))
        {
            await Results.Problem(
                title: "Missing or invalid CSRF token",
                detail: $"Send the csrfToken from GET /api/v1/auth/session in the {SessionAuthentication.CsrfHeader} header.",
                statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(context);
            return;
        }

        await next(context);
    });
}
