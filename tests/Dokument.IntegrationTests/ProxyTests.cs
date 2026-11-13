using System.Net;
using System.Net.Http.Json;
using Dokument.Server.Files;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using static Dokument.IntegrationTests.FileTests;

namespace Dokument.IntegrationTests;

public sealed class ProxyTests(PostgresFixture postgres)
{
    private static readonly FakeUser Alice = new(
        "alice-sub",
        "Alice Andersson",
        "alice@example.com"
    );

    [Fact]
    public async Task Visitors_behind_a_trusted_proxy_are_counted_apart()
    {
        await using var app = await StartBehindProxyAsync(
            new Dictionary<string, string?> { ["TRUSTED_PROXIES"] = "10.0.0.0/8" }
        );
        var token = await LockedLinkAsync(app);

        // Ten wrong passwords from one visitor use up their tries; the next visitor still gets theirs.
        using var first = app.CreateBrowser();
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await UnlockAsync(first, token, "203.0.113.1")).StatusCode
            );
        }

        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await UnlockAsync(first, token, "203.0.113.1")).StatusCode
        );
        using var second = app.CreateBrowser();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await UnlockAsync(second, token, "203.0.113.2")).StatusCode
        );
    }

    [Fact]
    public async Task An_untrusted_proxy_cannot_pick_the_visitor_address()
    {
        await using var app = await StartBehindProxyAsync(null);
        var token = await LockedLinkAsync(app);

        // The proxy at 10.0.0.5 isn't trusted, so a made-up X-Forwarded-For doesn't buy more tries.
        using var browser = app.CreateBrowser();
        for (var i = 0; i < 10; i++)
        {
            await UnlockAsync(browser, token, $"203.0.113.{i + 1}");
        }

        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await UnlockAsync(browser, token, "198.51.100.7")).StatusCode
        );
    }

    /// <summary>The app as a reverse proxy at 10.0.0.5 sees it: every request comes from that address.</summary>
    private Task<DokumentApp> StartBehindProxyAsync(Dictionary<string, string?>? settings) =>
        DokumentApp.StartAsync(
            postgres,
            settings,
            services => services.AddTransient<IStartupFilter, FromProxy>()
        );

    private static async Task<string> LockedLinkAsync(DokumentApp app)
    {
        using var alice = await SignInAsync(app, Alice);
        var file = await UploadAsync(alice, "lease.txt", "lease"u8.ToArray(), null);
        var response = await alice.PutAsJsonAsync(
            $"/api/v1/nodes/{file}/link",
            new SaveLinkRequest(false, null, "semester")
        );
        response.EnsureSuccessStatusCode();
        var link = (await response.Content.ReadFromJsonAsync<LinkDto>(Json.Options))!;
        return link.Url[(link.Url.IndexOf("/s/", StringComparison.Ordinal) + 3)..];
    }

    private static Task<HttpResponseMessage> UnlockAsync(
        HttpClient client,
        string token,
        string forwardedFor
    )
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/links/{token}/unlock")
        {
            Content = JsonContent.Create(new UnlockLinkRequest("wrong")),
        };
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        return client.SendAsync(request);
    }

    private sealed class FromProxy : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(
                    (context, nextMiddleware) =>
                    {
                        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");
                        return nextMiddleware(context);
                    }
                );
                next(app);
            };
    }
}
