using System.Net;
using System.Net.Http.Json;
using Dokument.Domain.Identity;
using Dokument.Infrastructure.Identity;
using Dokument.Server.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Dokument.IntegrationTests;

public sealed class AuthTests(PostgresFixture postgres)
{
    private static readonly FakeUser Alice = new(
        "alice-sub",
        "Alice Andersson",
        "alice@example.com",
        "family"
    );
    private static readonly FakeUser Bob = new(
        "bob-sub",
        "Bob Berg",
        "bob@example.com",
        "dokument-admins"
    );

    [Fact]
    public async Task Login_creates_the_user_and_a_cookie_session()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var browser = app.CreateBrowser();

        var callback = await app.LoginAsync(browser, Alice, returnTo: "/folders/42");

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/folders/42", callback.Headers.Location!.ToString());
        var cookie = Assert.Single(
            callback.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith(SessionAuthentication.CookieName + "=", StringComparison.Ordinal)
        );
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);

        var session = await DokumentApp.GetSessionAsync(browser);
        Assert.Equal("Alice Andersson", session.User.DisplayName);
        Assert.Equal("alice@example.com", session.User.Email);
        Assert.Equal(UserRole.User, session.User.Role);
        Assert.NotNull(session.CsrfToken);
    }

    [Fact]
    public async Task Admin_group_maps_to_admin_and_logins_reuse_the_user()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var first = app.CreateBrowser();
        using var second = app.CreateBrowser();

        await app.LoginAsync(first, Bob with { Groups = [] });
        Assert.Equal(UserRole.User, (await DokumentApp.GetSessionAsync(first)).User.Role);

        await app.LoginAsync(second, Bob);
        var session = await DokumentApp.GetSessionAsync(second);
        Assert.Equal(UserRole.Admin, session.User.Role);
        Assert.Equal((await DokumentApp.GetSessionAsync(first)).User.Id, session.User.Id);

        var sessions = await second.GetFromJsonAsync<List<SessionInfo>>(
            "/api/v1/sessions",
            Json.Options
        );
        Assert.Equal(2, sessions!.Count);
        Assert.Single(sessions, s => s.Current);
    }

    [Fact]
    public async Task User_group_is_an_allow_list()
    {
        await using var app = await DokumentApp.StartAsync(
            postgres,
            new Dictionary<string, string?> { ["OIDC_USER_GROUP"] = "dokument-users" }
        );
        using var browser = app.CreateBrowser();

        var callback = await app.LoginAsync(browser, Alice);

        Assert.Equal("/signin?error=not_allowed", callback.Headers.Location!.ToString());
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await browser.GetAsync("/api/v1/auth/session")).StatusCode
        );
    }

    [Fact]
    public async Task Disabled_users_cannot_log_in()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var browser = app.CreateBrowser();
        await app.LoginAsync(browser, Alice);
        var userId = (await DokumentApp.GetSessionAsync(browser)).User.Id;

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db =
                scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.DokumentDbContext>();
            (await db.Users.FindAsync(userId))!.DisabledAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await browser.GetAsync("/api/v1/auth/session")).StatusCode
        );
        using var again = app.CreateBrowser();
        Assert.Equal(
            "/signin?error=disabled",
            (await app.LoginAsync(again, Alice)).Headers.Location!.ToString()
        );
    }

    [Fact]
    public async Task Return_url_cannot_leave_the_site()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var browser = app.CreateBrowser();

        var callback = await app.LoginAsync(browser, Alice, returnTo: "//evil.example/phish");

        Assert.Equal("/", callback.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Cookie_mutations_need_the_csrf_token()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var browser = app.CreateBrowser();
        await app.LoginAsync(browser, Alice);
        var session = await DokumentApp.GetSessionAsync(browser);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await browser.PostAsync("/api/v1/auth/logout", null)).StatusCode
        );

        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        logout.Headers.Add(SessionAuthentication.CsrfHeader, session.CsrfToken);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.SendAsync(logout)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await browser.GetAsync("/api/v1/auth/session")).StatusCode
        );
    }

    [Fact]
    public async Task Revoked_sessions_stop_working()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var laptop = app.CreateBrowser();
        using var phone = app.CreateBrowser();
        await app.LoginAsync(laptop, Alice);
        await app.LoginAsync(phone, Alice);
        var laptopSession = await DokumentApp.GetSessionAsync(laptop);
        var phoneSession = await DokumentApp.GetSessionAsync(phone);

        using var revoke = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/api/v1/sessions/{phoneSession.SessionId}"
        );
        revoke.Headers.Add(SessionAuthentication.CsrfHeader, laptopSession.CsrfToken);
        Assert.Equal(HttpStatusCode.NoContent, (await laptop.SendAsync(revoke)).StatusCode);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await phone.GetAsync("/api/v1/auth/session")).StatusCode
        );
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/api/v1/auth/session")).StatusCode);
    }

    [Fact]
    public async Task Users_cannot_revoke_other_peoples_sessions_or_use_admin_endpoints()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var alice = app.CreateBrowser();
        using var bob = app.CreateBrowser();
        await app.LoginAsync(alice, Alice);
        await app.LoginAsync(bob, Bob);
        var aliceSession = await DokumentApp.GetSessionAsync(alice);
        var bobSession = await DokumentApp.GetSessionAsync(bob);

        using var revoke = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/api/v1/sessions/{bobSession.SessionId}"
        );
        revoke.Headers.Add(SessionAuthentication.CsrfHeader, aliceSession.CsrfToken);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.SendAsync(revoke)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await alice.GetAsync($"/api/v1/admin/users/{bobSession.User.Id}/sessions")).StatusCode
        );

        // Bob is an admin and may revoke Alice's session.
        var aliceSessions = await bob.GetFromJsonAsync<List<SessionInfo>>(
            $"/api/v1/admin/users/{aliceSession.User.Id}/sessions",
            Json.Options
        );
        Assert.Single(aliceSessions!);
        using var adminRevoke = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/api/v1/admin/sessions/{aliceSession.SessionId}"
        );
        adminRevoke.Headers.Add(SessionAuthentication.CsrfHeader, bobSession.CsrfToken);
        Assert.Equal(HttpStatusCode.NoContent, (await bob.SendAsync(adminRevoke)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await alice.GetAsync("/api/v1/auth/session")).StatusCode
        );
    }

    [Fact]
    public async Task Bearer_sessions_need_no_csrf_token()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var browser = app.CreateBrowser();
        await app.LoginAsync(browser, Alice);
        var userId = (await DokumentApp.GetSessionAsync(browser)).User.Id;

        SessionToken token;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            (_, token) = await scope
                .ServiceProvider.GetRequiredService<SessionStore>()
                .CreateAsync(userId, SessionKind.Bearer, DeviceName.From("Pixel 9"));
        }

        using var phone = app.CreateClient();
        phone.DefaultRequestHeaders.Authorization = new("Bearer", token.Value);
        var session = (
            await phone.GetFromJsonAsync<CurrentSession>("/api/v1/auth/session", Json.Options)
        )!;
        Assert.Null(session.CsrfToken);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await phone.PostAsync("/api/v1/auth/logout", null)).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await phone.GetAsync("/api/v1/auth/session")).StatusCode
        );
    }

    [Fact]
    public async Task Unauthenticated_api_calls_get_401()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var client = app.CreateClient();

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/v1/sessions")).StatusCode
        );
        var config = await client.GetFromJsonAsync<AuthConfig>("/api/v1/auth/config", Json.Options);
        Assert.True(config!.Enabled);
    }
}
