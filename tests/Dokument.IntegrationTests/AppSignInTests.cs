using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Dokument.Server.Auth;
using Microsoft.IdentityModel.Tokens;

namespace Dokument.IntegrationTests;

/// <summary>The mobile app signs in through an https bounce and checks local addresses with a proof (§5, §15.3).</summary>
public sealed class AppSignInTests(PostgresFixture postgres)
{
    private const string AppRedirectUri = "http://localhost/api/v1/auth/app-callback";

    private static readonly FakeUser Alice = new(
        "alice-sub",
        "Alice Andersson",
        "alice@example.com"
    );

    [Fact]
    public async Task Auth_config_tells_the_app_how_to_sign_in()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var client = app.CreateClient();

        var config = (
            await client.GetFromJsonAsync<AuthConfig>("/api/v1/auth/config", Json.Options)
        )!;

        Assert.Equal(FakeOidcProvider.Issuer, config.Issuer);
        Assert.Equal(FakeOidcProvider.ClientId, config.ClientId);
        Assert.Equal("openid profile email", config.Scope);
        Assert.Equal(AppRedirectUri, config.AppRedirectUri);
    }

    [Fact]
    public async Task The_callback_hands_the_idps_answer_to_the_app()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var browser = app.CreateBrowser();

        var code = await browser.GetAsync("/api/v1/auth/app-callback?code=abc&state=s1");
        var refused = await browser.GetAsync(
            "/api/v1/auth/app-callback?error=access_denied&state=s2"
        );

        Assert.Equal(HttpStatusCode.Redirect, code.StatusCode);
        Assert.Equal(
            new Uri("dokumentsystem://signed-in?code=abc&state=s1"),
            code.Headers.Location
        );
        Assert.Equal(
            new Uri("dokumentsystem://signed-in?error=access_denied&state=s2"),
            refused.Headers.Location
        );
    }

    [Fact]
    public async Task The_app_signs_in_with_its_redirect_uri_and_names_the_phone()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        var (token, _) = await SignInAsAppAsync(app, "Pixel 9");
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var devices = (await client.GetFromJsonAsync<JsonArray>("/api/v1/sessions"))!;

        Assert.Equal("Pixel 9", (string?)Assert.Single(devices)!["deviceName"]);
    }

    [Fact]
    public async Task A_local_address_proves_the_session_without_the_token()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        var (token, sessionId) = await SignInAsAppAsync(app, "Pixel 9");
        using var client = app.CreateClient();
        var nonce = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

        var proof = await client.PostAsJsonAsync("/api/v1/auth/proof", new { sessionId, nonce });
        var unknown = await client.PostAsJsonAsync(
            "/api/v1/auth/proof",
            new { sessionId = Guid.NewGuid(), nonce }
        );
        var badNonce = await client.PostAsJsonAsync(
            "/api/v1/auth/proof",
            new { sessionId, nonce = "short" }
        );

        // What the app computes from its token: an HMAC of the nonce keyed by the token's SHA-256.
        var expected = Base64UrlEncoder.Encode(
            HMACSHA256.HashData(
                SHA256.HashData(Encoding.UTF8.GetBytes(token)),
                Encoding.UTF8.GetBytes(nonce)
            )
        );
        proof.EnsureSuccessStatusCode();
        Assert.Equal(expected, (await proof.Content.ReadFromJsonAsync<SessionProof>())!.Proof);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badNonce.StatusCode);
    }

    [Fact]
    public async Task Only_the_live_hub_reads_the_token_from_the_query()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        var (token, _) = await SignInAsAppAsync(app, "Pixel 9");
        using var client = app.CreateClient();

        var live = await client.PostAsync(
            $"/api/v1/live/negotiate?negotiateVersion=1&access_token={token}",
            null
        );
        var elsewhere = await client.GetAsync($"/api/v1/sessions?access_token={token}");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, elsewhere.StatusCode);
    }

    private static async Task<(string Token, Guid SessionId)> SignInAsAppAsync(
        DokumentApp app,
        string deviceName
    )
    {
        var (code, verifier) = await TokenEndpointTests.SignInAtIdpAsync(
            app,
            Alice,
            AppRedirectUri
        );
        using var client = app.CreateClient();
        var response = await client.PostAsync(
            "/api/v1/auth/token",
            TokenEndpointTests.TokenRequest(code, verifier, AppRedirectUri, deviceName)
        );
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var session = (
            await client.GetFromJsonAsync<CurrentSession>("/api/v1/auth/session", Json.Options)
        )!;
        return (token, session.SessionId);
    }
}
