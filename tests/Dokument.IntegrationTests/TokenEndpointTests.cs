using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Dokument.Domain.Identity;
using Dokument.Server.Auth;
using Microsoft.IdentityModel.Tokens;

namespace Dokument.IntegrationTests;

/// <summary>Clients that sign in at the IdP themselves (Scalar, later the app) redeem codes with us.</summary>
public sealed class TokenEndpointTests(PostgresFixture postgres)
{
    private const string ScalarRedirectUri = "http://localhost/scalar/";

    private static readonly FakeUser Bob = new(
        "bob-sub",
        "Bob Berg",
        "bob@example.com",
        "dokument-admins"
    );

    [Fact]
    public async Task A_code_is_redeemed_for_a_bearer_session()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        var (code, verifier) = await SignInAtIdpAsync(app, Bob);
        using var client = app.CreateClient();

        var response = await client.PostAsync(
            "/api/v1/auth/token",
            TokenRequest(code, verifier, ScalarRedirectUri)
        );

        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
        Assert.Equal("Bearer", token.TokenType);
        Assert.True(token.ExpiresIn > 0);

        client.DefaultRequestHeaders.Authorization = new("Bearer", token.AccessToken);
        var session = (
            await client.GetFromJsonAsync<CurrentSession>("/api/v1/auth/session", Json.Options)
        )!;
        Assert.Equal("Bob Berg", session.User.DisplayName);
        Assert.Equal(UserRole.Admin, session.User.Role);
        Assert.Null(session.CsrfToken);

        // A bearer session: mutations need no CSRF token, and it shows up under active devices.
        var folder = await client.PostAsJsonAsync("/api/v1/folders", new { name = "Kvitton" });
        Assert.Equal(HttpStatusCode.Created, folder.StatusCode);
        var devices = await client.GetFromJsonAsync<JsonArray>("/api/v1/sessions");
        Assert.Single(devices!);
    }

    [Fact]
    public async Task A_wrong_verifier_is_refused()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        var (code, _) = await SignInAtIdpAsync(app, Bob);
        using var client = app.CreateClient();

        var response = await client.PostAsync(
            "/api/v1/auth/token",
            TokenRequest(code, "not-the-verifier", ScalarRedirectUri)
        );

        await AssertErrorAsync(response, "invalid_grant");
    }

    [Fact]
    public async Task Codes_for_other_redirect_uris_are_refused()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        const string elsewhere = "https://evil.example/scalar/";
        var (code, verifier) = await SignInAtIdpAsync(app, Bob, elsewhere);
        using var client = app.CreateClient();

        var response = await client.PostAsync(
            "/api/v1/auth/token",
            TokenRequest(code, verifier, elsewhere)
        );

        await AssertErrorAsync(response, "invalid_grant");
    }

    [Fact]
    public async Task People_outside_the_user_group_are_refused()
    {
        await using var app = await DokumentApp.StartAsync(
            postgres,
            new Dictionary<string, string?> { ["OIDC_USER_GROUP"] = "family" }
        );
        var (code, verifier) = await SignInAtIdpAsync(app, Bob with { Groups = [] });
        using var client = app.CreateClient();

        var response = await client.PostAsync(
            "/api/v1/auth/token",
            TokenRequest(code, verifier, ScalarRedirectUri)
        );

        await AssertErrorAsync(response, "access_denied");
    }

    [Fact]
    public async Task OpenApi_document_describes_the_code_flow()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var client = app.CreateClient();

        var document = (await client.GetFromJsonAsync<JsonObject>("/openapi/v1.json"))!;

        var flow = document["components"]!["securitySchemes"]!["OAuth2"]!["flows"]![
            "authorizationCode"
        ]!;
        Assert.Equal($"{FakeOidcProvider.Issuer}/authorize", (string?)flow["authorizationUrl"]);
        Assert.Equal("http://localhost/api/v1/auth/token", (string?)flow["tokenUrl"]);
        Assert.NotNull(document["security"]![0]!["OAuth2"]);
    }

    /// <summary>What Scalar's popup does: sign in at the IdP with PKCE and take the code from the redirect.</summary>
    internal static async Task<(string Code, string Verifier)> SignInAtIdpAsync(
        DokumentApp app,
        FakeUser user,
        string redirectUri = ScalarRedirectUri
    )
    {
        app.Idp.NextUser = user;
        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        using var idpBrowser = app.Idp.CreateBrowser();
        var authorize = await idpBrowser.GetAsync(
            $"/authorize?client_id={FakeOidcProvider.ClientId}&response_type=code&code_challenge_method=S256"
                + $"&code_challenge={challenge}&redirect_uri={Uri.EscapeDataString(redirectUri)}&state=s"
        );
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var query = System.Web.HttpUtility.ParseQueryString(authorize.Headers.Location!.Query);
        return (query["code"]!, verifier);
    }

    internal static FormUrlEncodedContent TokenRequest(
        string code,
        string verifier,
        string redirectUri,
        string? deviceName = null
    ) =>
        new(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = FakeOidcProvider.ClientId,
                ["device_name"] = deviceName ?? "",
            }
        );

    private static async Task AssertErrorAsync(HttpResponseMessage response, string error)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<TokenError>())!;
        Assert.Equal(error, body.Error);
    }
}
