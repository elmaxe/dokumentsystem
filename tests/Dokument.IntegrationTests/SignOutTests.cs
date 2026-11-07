using System.Net;
using System.Net.Http.Json;
using Dokument.Server.Auth;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Dokument.IntegrationTests;

/// <summary>Signing out ends our session and, through the IdP's end-session endpoint, the IdP's (docs/design.md §5).</summary>
public sealed class SignOutTests(PostgresFixture postgres)
{
    private static readonly FakeUser Alice = new(
        "alice-sub",
        "Alice Andersson",
        "alice@example.com"
    );

    [Fact]
    public async Task Web_sign_out_sends_the_browser_to_the_idp_with_the_id_token()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var browser = app.CreateBrowser();
        await app.LoginAsync(browser, Alice);

        var result = await LogoutAsync(browser);

        var url = new Uri(result.EndSessionUrl!);
        Assert.Equal($"{FakeOidcProvider.Issuer}/logout", url.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(url.Query);
        Assert.Equal(FakeOidcProvider.ClientId, query["client_id"]);
        Assert.Equal("http://localhost/api/v1/auth/signed-out", query["post_logout_redirect_uri"]);
        Assert.Equal("alice-sub", new JsonWebToken(query["id_token_hint"]!).Subject);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await browser.GetAsync("/api/v1/auth/session")).StatusCode
        );
    }

    [Fact]
    public async Task App_sign_out_returns_to_the_app()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        const string redirectUri = "http://localhost/api/v1/auth/app-callback";
        var (code, verifier) = await TokenEndpointTests.SignInAtIdpAsync(app, Alice, redirectUri);
        using var phone = app.CreateClient();
        var token = (
            await (
                await phone.PostAsync(
                    "/api/v1/auth/token",
                    TokenEndpointTests.TokenRequest(code, verifier, redirectUri, "Pixel 9")
                )
            ).Content.ReadFromJsonAsync<TokenResponse>()
        )!;
        phone.DefaultRequestHeaders.Authorization = new("Bearer", token.AccessToken);

        var response = await phone.PostAsync("/api/v1/auth/logout", null);

        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<SignOutResult>(Json.Options))!;
        var query = QueryHelpers.ParseQuery(new Uri(result.EndSessionUrl!).Query);
        Assert.Equal(
            "http://localhost/api/v1/auth/app-signed-out",
            query["post_logout_redirect_uri"]
        );
        Assert.Equal("alice-sub", new JsonWebToken(query["id_token_hint"]!).Subject);
    }

    [Fact]
    public async Task Without_an_end_session_endpoint_sign_out_stays_local()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        app.Idp.SupportsEndSession = false;
        using var browser = app.CreateBrowser();
        await app.LoginAsync(browser, Alice);

        var result = await LogoutAsync(browser);

        Assert.Null(result.EndSessionUrl);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await browser.GetAsync("/api/v1/auth/session")).StatusCode
        );
    }

    [Fact]
    public async Task The_idp_returns_to_the_sign_in_page_or_the_app()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var browser = app.CreateBrowser();

        var web = await browser.GetAsync("/api/v1/auth/signed-out");
        var phone = await browser.GetAsync("/api/v1/auth/app-signed-out");

        Assert.Equal(HttpStatusCode.Redirect, web.StatusCode);
        Assert.Equal("/signin?reason=signedOut", web.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.Redirect, phone.StatusCode);
        Assert.Equal(SignOut.AppReturnUrl, phone.Headers.Location!.OriginalString);
    }

    private static async Task<SignOutResult> LogoutAsync(HttpClient browser)
    {
        var session = await DokumentApp.GetSessionAsync(browser);
        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        logout.Headers.Add(SessionAuthentication.CsrfHeader, session.CsrfToken);
        var response = await browser.SendAsync(logout);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SignOutResult>(Json.Options))!;
    }
}
