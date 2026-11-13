using System.Net;
using System.Net.Http.Json;
using Dokument.Links;
using Dokument.Server.Files;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using static Dokument.IntegrationTests.FileTests;

namespace Dokument.IntegrationTests;

/// <summary>Public links served from outside, through the links gateway and the server's links port (§9.1).</summary>
public sealed class LinksGatewayTests(PostgresFixture postgres)
{
    private const int LinksPort = 8081;

    private static readonly FakeUser Alice = new(
        "alice-sub",
        "Alice Andersson",
        "alice@example.com"
    );

    [Fact]
    public async Task The_links_port_answers_only_the_link_api()
    {
        await using var app = await StartAsync();
        using var alice = await SignInAsync(app, Alice);
        var file = await UploadAsync(alice, "lease.txt", "lease"u8.ToArray(), null);
        var link = await SaveLinkAsync(alice, file, null);

        // The address to copy is the gateway's, not the one Alice uses.
        Assert.StartsWith("https://links.example.com/s/", link.Url);
        var token = link.Url["https://links.example.com/s/".Length..];

        using var gatewaySide = app.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri($"http://server:{LinksPort}"),
                HandleCookies = false,
            }
        );
        (await gatewaySide.GetAsync($"/api/v1/links/{token}")).EnsureSuccessStatusCode();
        (await gatewaySide.GetAsync("/health/live")).EnsureSuccessStatusCode();
        foreach (
            var path in new[]
            {
                "/",
                "/s/" + token,
                "/api/v1/server/info",
                "/api/v1/auth/config",
                "/api/v1/auth/login",
                $"/api/v1/nodes/{file}/link",
                "/health/ready",
                "/openapi/v1.json",
            }
        )
        {
            Assert.Equal(HttpStatusCode.NotFound, (await gatewaySide.GetAsync(path)).StatusCode);
        }

        // The main port is as before.
        (await alice.GetAsync("/api/v1/server/info")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Visitors_through_the_gateway_see_links_and_nothing_else()
    {
        await using var app = await StartAsync();
        using var alice = await SignInAsync(app, Alice);
        var file = await UploadAsync(alice, "lease.txt", "lease"u8.ToArray(), null);
        var link = await SaveLinkAsync(alice, file, "semester");
        var token = link.Url[(link.Url.IndexOf("/s/", StringComparison.Ordinal) + 3)..];

        var webRoot = Directory.CreateTempSubdirectory("dokument-links-").FullName;
        await File.WriteAllTextAsync(Path.Combine(webRoot, "link.html"), "<div id=\"root\"></div>");
        await using var gateway = await StartGatewayAsync(app, webRoot);
        using var visitor = gateway.GetTestClient();

        // The page, then the API: locked until the password is typed.
        var page = await visitor.GetAsync($"/s/{token}");
        page.EnsureSuccessStatusCode();
        Assert.Contains("id=\"root\"", await page.Content.ReadAsStringAsync());
        Assert.Equal("no-referrer", page.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await visitor.GetAsync($"/api/v1/links/{token}")).StatusCode
        );
        var unlocked = await visitor.PostAsJsonAsync(
            $"/api/v1/links/{token}/unlock",
            new UnlockLinkRequest("semester")
        );
        unlocked.EnsureSuccessStatusCode();
        var cookie = unlocked.Headers.GetValues("Set-Cookie").Single().Split(';')[0];

        using var open = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/links/{token}");
        open.Headers.Add("Cookie", cookie);
        var opened = await visitor.SendAsync(open);
        opened.EnsureSuccessStatusCode();
        Assert.Equal(
            "lease.txt",
            (await opened.Content.ReadFromJsonAsync<LinkPage>(Json.Options))!.Node.Name
        );

        using var download = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/links/{token}/nodes/{file}/content"
        );
        download.Headers.Add("Cookie", cookie);
        download.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1, 3);
        var part = await visitor.SendAsync(download);
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal("eas", await part.Content.ReadAsStringAsync());

        // Nothing but the link API gets through, and only in the shape a link uses.
        foreach (
            var path in new[]
            {
                "/",
                "/api/v1/server/info",
                "/api/v1/auth/login",
                $"/api/v1/nodes/{file}",
                "/api/v1/links/not-a-token",
                "/link.css",
            }
        )
        {
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(path)).StatusCode);
        }

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await visitor.PostAsync($"/api/v1/links/{token}/nodes", null)).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await visitor.DeleteAsync($"/api/v1/links/{token}")).StatusCode
        );
    }

    [Fact]
    public async Task Each_visitor_through_the_gateway_has_their_own_tries()
    {
        await using var app = await StartAsync();
        using var alice = await SignInAsync(app, Alice);
        var file = await UploadAsync(alice, "lease.txt", "lease"u8.ToArray(), null);
        var link = await SaveLinkAsync(alice, file, "semester");
        var token = link.Url[(link.Url.IndexOf("/s/", StringComparison.Ordinal) + 3)..];
        await using var gateway = await StartGatewayAsync(app, null);

        // The gateway passes on who it was talking to; the server counts each of them apart.
        async Task<HttpStatusCode> TryAsync(string from)
        {
            var context = await gateway
                .GetTestServer()
                .SendAsync(context =>
                {
                    context.Request.Method = HttpMethods.Post;
                    context.Request.Path = $"/api/v1/links/{token}/unlock";
                    context.Request.ContentType = "application/json";
                    context.Request.Body = new MemoryStream("""{"password":"wrong"}"""u8.ToArray());
                    context.Connection.RemoteIpAddress = IPAddress.Parse(from);
                });
            return (HttpStatusCode)context.Response.StatusCode;
        }

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await TryAsync("203.0.113.1"));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await TryAsync("203.0.113.1"));
        Assert.Equal(HttpStatusCode.Unauthorized, await TryAsync("203.0.113.2"));
    }

    private Task<DokumentApp> StartAsync() =>
        DokumentApp.StartAsync(
            postgres,
            new Dictionary<string, string?>
            {
                [PublicLinks.PortSetting] = LinksPort.ToString(
                    System.Globalization.CultureInfo.InvariantCulture
                ),
                [PublicLinks.PublicUrlSetting] = "https://links.example.com",
            },
            services => services.AddTransient<IStartupFilter, OnLinksPort>()
        );

    private static async Task<WebApplication> StartGatewayAsync(DokumentApp app, string? webRoot)
    {
        var builder = LinksGateway.CreateBuilder([
            $"--{LinksGateway.UpstreamSetting}=http://server:{LinksPort}",
            .. webRoot is null ? Array.Empty<string>() : [$"--webroot={webRoot}"],
        ]);
        builder.WebHost.UseTestServer();
        builder
            .Services.AddHttpClient(LinksGateway.UpstreamClient)
            .ConfigurePrimaryHttpMessageHandler(() => app.Server.CreateHandler());
        var gateway = LinksGateway.Build(builder);
        await gateway.StartAsync();
        return gateway;
    }

    private static async Task<LinkDto> SaveLinkAsync(HttpClient client, Guid file, string? password)
    {
        var response = await client.PutAsJsonAsync(
            $"/api/v1/nodes/{file}/link",
            new SaveLinkRequest(true, null, password)
        );
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LinkDto>(Json.Options))!;
    }

    /// <summary>The test server has no ports, so a request to host port 8081 stands for one on the links port.</summary>
    private sealed class OnLinksPort : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(
                    (context, nextMiddleware) =>
                    {
                        context.Connection.LocalPort = context.Request.Host.Port ?? 80;
                        return nextMiddleware(context);
                    }
                );
                next(app);
            };
    }
}
