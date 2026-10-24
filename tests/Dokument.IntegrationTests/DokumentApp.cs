using System.Net;
using System.Net.Http.Json;
using Dokument.Server.Auth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Dokument.IntegrationTests;

/// <summary>The real app, with its own database, signing in against <see cref="FakeOidcProvider"/>.</summary>
public sealed class DokumentApp(
    PostgresFixture postgres,
    string connectionString,
    FakeOidcProvider idp,
    IReadOnlyDictionary<string, string?>? settings = null
) : WebApplicationFactory<Program>
{
    private readonly string dataRoot = Directory.CreateTempSubdirectory("dokument-test-").FullName;

    public FakeOidcProvider Idp => idp;

    public string OriginalsPath => Path.Combine(dataRoot, "originals");

    public static async Task<DokumentApp> StartAsync(
        PostgresFixture postgres,
        IReadOnlyDictionary<string, string?>? settings = null
    )
    {
        var app = new DokumentApp(
            postgres,
            await postgres.CreateDatabaseAsync(),
            await FakeOidcProvider.StartAsync(),
            settings
        );
        app.StartServer();
        return app;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Dokument", connectionString);
        builder.UseSetting("Storage:OriginalsPath", Path.Combine(dataRoot, "originals"));
        builder.UseSetting("Storage:DerivedPath", Path.Combine(dataRoot, "derived"));
        builder.UseSetting("Storage:BackupsPath", Path.Combine(dataRoot, "backups"));
        builder.UseSetting("OIDC_ISSUER", FakeOidcProvider.Issuer);
        builder.UseSetting("OIDC_CLIENT_ID", FakeOidcProvider.ClientId);
        builder.UseSetting("OIDC_CLIENT_SECRET", FakeOidcProvider.ClientSecret);
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
            services.Configure<OpenIdConnectOptions>(
                AuthSetup.OidcScheme,
                options => options.BackchannelHttpHandler = idp.Handler
            )
        );
    }

    /// <summary>A browser: keeps cookies and doesn't follow redirects, so each hop can be checked.</summary>
    public HttpClient CreateBrowser() =>
        CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = true,
            }
        );

    /// <summary>Runs the whole login: app → IdP → callback. Returns the callback's response.</summary>
    public async Task<HttpResponseMessage> LoginAsync(
        HttpClient browser,
        FakeUser user,
        string returnTo = "/"
    )
    {
        idp.NextUser = user;
        var start = await browser.GetAsync(
            $"/api/v1/auth/login?returnTo={Uri.EscapeDataString(returnTo)}"
        );
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        Assert.StartsWith(
            $"{FakeOidcProvider.Issuer}/authorize",
            start.Headers.Location!.ToString()
        );

        using var idpBrowser = idp.CreateBrowser();
        var authorize = await idpBrowser.GetAsync(start.Headers.Location);
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);

        return await browser.GetAsync(authorize.Headers.Location);
    }

    public static async Task<CurrentSession> GetSessionAsync(HttpClient browser)
    {
        var response = await browser.GetAsync("/api/v1/auth/session");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CurrentSession>(Json.Options))!;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        // Each test has its own database. Its pooled connections would sit idle until the pool prunes them, and enough
        // of them run Postgres out of connection slots, so drop it and them.
        await postgres.DropDatabaseAsync(connectionString);

        await idp.DisposeAsync();
        Directory.Delete(dataRoot, recursive: true);
    }
}

internal static class Json
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new(
        System.Text.Json.JsonSerializerDefaults.Web
    )
    {
        Converters =
        {
            new System.Text.Json.Serialization.JsonStringEnumConverter(
                System.Text.Json.JsonNamingPolicy.CamelCase
            ),
        },
    };
}
