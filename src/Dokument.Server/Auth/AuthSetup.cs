using System.Security.Claims;
using System.Threading.RateLimiting;
using Dokument.Domain.Identity;
using Dokument.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Dokument.Server.Auth;

/// <summary>OIDC login and app-issued sessions (docs/design.md §5).</summary>
public static class AuthSetup
{
    public const string OidcScheme = "oidc";
    public const string CallbackPath = "/api/v1/auth/callback";
    public const string AuthRateLimitPolicy = "auth";

    public static WebApplicationBuilder AddDokumentAuth(this WebApplicationBuilder builder)
    {
        var oidc = OidcSettings.FromConfiguration(builder.Configuration);
        builder.Services.AddSingleton(oidc);

        var auth = builder.Services
            .AddAuthentication(SessionAuthentication.Scheme)
            .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionAuthentication.Scheme, null);

        if (oidc.Enabled)
        {
            auth.AddOpenIdConnect(OidcScheme, options => ConfigureOidc(options, oidc, builder.Environment));
        }

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(SessionAuthentication.AdminPolicy, policy => policy.RequireAuthenticatedUser().RequireRole("admin"));

        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(AuthRateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
        });

        return builder;
    }

    private static void ConfigureOidc(OpenIdConnectOptions options, OidcSettings oidc, IHostEnvironment environment)
    {
        options.Authority = oidc.Issuer;
        options.ClientId = oidc.ClientId;
        options.ClientSecret = oidc.ClientSecret;
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;
        options.CallbackPath = CallbackPath;
        options.RequireHttpsMetadata = !environment.IsDevelopment();

        // A query response keeps the IdP's redirect a top-level GET, so the correlation cookies can be SameSite=Lax
        // and login also works over plain http on a LAN.
        options.ResponseMode = OpenIdConnectResponseMode.Query;
        options.CorrelationCookie.SameSite = SameSiteMode.Lax;
        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.NonceCookie.SameSite = SameSiteMode.Lax;
        options.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;

        // Keep the IdP's claim names (sub, email, groups) instead of mapping them to WS-* URIs.
        options.MapInboundClaims = false;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.ClaimActions.MapJsonKey(oidc.GroupsClaim, oidc.GroupsClaim);
        options.ClaimActions.MapUniqueJsonKey("preferred_username", "preferred_username");
        options.Scope.Clear();
        foreach (var scope in oidc.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            options.Scope.Add(scope);
        }

        // We issue our own session, so the IdP's tokens are dropped after login.
        options.SaveTokens = false;

        options.Events.OnTicketReceived = context => CompleteLoginAsync(context, oidc);
        options.Events.OnRemoteFailure = context =>
        {
            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(AuthSetup)).LogWarning(context.Failure, "OIDC login failed");
            context.Response.Redirect(SignInPage(context.Request, "failed"));
            context.HandleResponse();
            return Task.CompletedTask;
        };
    }

    /// <summary>Provisions the user, issues our own session cookie and returns to the page the user came from.</summary>
    private static async Task CompleteLoginAsync(TicketReceivedContext context, OidcSettings oidc)
    {
        var principal = context.Principal!;
        var http = context.HttpContext;
        var subject = principal.FindFirstValue("sub")
            ?? throw new InvalidOperationException("The IdP's ID token has no sub claim.");
        var identity = new OidcIdentity(
            Issuer: principal.FindFirstValue("iss") ?? principal.FindFirst("sub")!.Issuer,
            Subject: subject,
            Email: principal.FindFirstValue("email"),
            DisplayName: principal.FindFirstValue("name") ?? principal.FindFirstValue("preferred_username"),
            Groups: principal.FindAll(oidc.GroupsClaim).Select(c => c.Value).Distinct(StringComparer.Ordinal).ToList());

        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AuthSetup));
        var result = await http.RequestServices.GetRequiredService<UserProvisioner>()
            .ProvisionAsync(identity, oidc.Policy, http.RequestAborted);
        if (result.User is not { } user)
        {
            logger.LogWarning("Login refused for {Subject} from {Issuer}: {Reason}", identity.Subject, identity.Issuer, result.Failure);
            context.Response.Redirect(SignInPage(context.Request, result.Failure == LoginFailure.Disabled ? "disabled" : "not_allowed"));
            context.HandleResponse();
            return;
        }

        var (session, token) = await http.RequestServices.GetRequiredService<SessionStore>().CreateAsync(
            user.Id, SessionKind.Cookie, DeviceNames.FromUserAgent(context.Request.Headers.UserAgent), http.RequestAborted);
        SessionAuthentication.SetCookie(http, token, session.ExpiresAt);
        logger.LogInformation("User {UserId} signed in as {Role}", user.Id, user.Role);

        context.Response.Redirect(context.Request.PathBase + ReturnUrl.Sanitize(context.ReturnUri));
        context.HandleResponse();
    }

    private static string SignInPage(HttpRequest request, string error) => $"{request.PathBase}/signin?error={error}";
}
