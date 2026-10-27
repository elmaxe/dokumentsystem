using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dokument.Domain.Identity;
using Dokument.Server.Auth;

namespace Dokument.IntegrationTests;

/// <summary>The API reference (Scalar) signs in at the IdP and calls the API with its access token.</summary>
public sealed class AccessTokenTests(PostgresFixture postgres)
{
    private static readonly Dictionary<string, string?> ScalarClient = new()
    {
        ["OIDC_SCALAR_CLIENT_ID"] = "dokument-scalar",
    };

    private static readonly FakeUser Alice = new(
        "alice-sub",
        "Alice Andersson",
        "alice@example.com",
        "dokument-admins"
    );

    [Fact]
    public async Task Access_tokens_sign_in_without_a_session_or_csrf_token()
    {
        await using var app = await DokumentApp.StartAsync(postgres, ScalarClient);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new(
            "Bearer",
            app.Idp.CreateAccessToken(Alice)
        );

        var session = (
            await client.GetFromJsonAsync<CurrentSession>("/api/v1/auth/session", Json.Options)
        )!;
        Assert.Equal("Alice Andersson", session.User.DisplayName);
        Assert.Equal(UserRole.Admin, session.User.Role);
        Assert.Equal(Guid.Empty, session.SessionId);
        Assert.Null(session.CsrfToken);

        var folder = await client.PostAsJsonAsync("/api/v1/folders", new { name = "Kvitton" });
        Assert.Equal(HttpStatusCode.Created, folder.StatusCode);
    }

    [Fact]
    public async Task Access_tokens_for_another_audience_are_refused()
    {
        await using var app = await DokumentApp.StartAsync(postgres, ScalarClient);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new(
            "Bearer",
            app.Idp.CreateAccessToken(Alice, audience: "account")
        );

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/v1/auth/session")).StatusCode
        );
    }

    [Fact]
    public async Task Access_tokens_are_refused_without_the_scalar_client()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new(
            "Bearer",
            app.Idp.CreateAccessToken(Alice)
        );

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/v1/auth/session")).StatusCode
        );
    }

    [Fact]
    public async Task OpenApi_document_describes_the_idps_code_flow()
    {
        await using var app = await DokumentApp.StartAsync(postgres, ScalarClient);
        using var client = app.CreateClient();

        var document = (await client.GetFromJsonAsync<JsonObject>("/openapi/v1.json"))!;

        var flow = document["components"]!["securitySchemes"]!["OAuth2"]!["flows"]![
            "authorizationCode"
        ]!;
        Assert.Equal($"{FakeOidcProvider.Issuer}/authorize", (string?)flow["authorizationUrl"]);
        Assert.Equal($"{FakeOidcProvider.Issuer}/token", (string?)flow["tokenUrl"]);
        Assert.NotNull(document["security"]![0]!["OAuth2"]);
    }
}
