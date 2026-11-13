using System.Net;
using System.Net.Http.Json;
using Dokument.Domain.Jobs;
using Dokument.Infrastructure.Persistence;
using Dokument.Server.Files;
using Dokument.Tests;
using Dokument.Worker.Avatars;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetVips;

namespace Dokument.IntegrationTests;

/// <summary>Profile pictures from the sign-in provider's picture claim (docs/design.md §5.1).</summary>
public sealed class AvatarTests(PostgresFixture postgres)
{
    private static readonly Dictionary<string, string?> FastWorker = new()
    {
        ["Worker:PollInterval"] = "00:00:00.100",
    };

    private static FakeUser Alice(string? picture) =>
        new("alice-sub", "Alice Andersson", "alice@example.com")
        {
            Claims = picture is null
                ? new Dictionary<string, object>()
                : new Dictionary<string, object> { ["picture"] = picture },
        };

    private static readonly FakeUser Bob = new("bob-sub", "Bob Lind", "bob@example.com");

    /// <summary>Starts the app with the worker's picture downloads going to the fake IdP.</summary>
    private async Task<DokumentApp> StartAsync(Dictionary<string, string?>? settings = null)
    {
        DokumentApp? app = null;
        app = await DokumentApp.StartAsync(
            postgres,
            new Dictionary<string, string?>(FastWorker.Concat(settings ?? [])),
            services =>
                services
                    .AddHttpClient(AvatarFetcher.ClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => app!.Idp.Handler)
        );
        return app;
    }

    [Fact]
    public async Task A_picture_from_the_idp_is_stored_and_served()
    {
        await using var app = await StartAsync();
        app.Idp.Pictures["alice.png"] = TestFiles.Png(400, 300);
        using var alice = await FileTests.SignInAsync(
            app,
            Alice($"{FakeOidcProvider.Issuer}/pictures/alice.png")
        );
        using var bob = await FileTests.SignInAsync(app, Bob);

        var session = await WaitForAvatarAsync(alice, present: true);
        var url = $"/api/v1/people/{session.User.Id}/avatar?v={session.User.Avatar}";

        // Anyone signed in sees it, cached for good under its versioned URL.
        var response = await bob.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/webp", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("immutable", response.Headers.CacheControl!.ToString());
        using (var image = Image.NewFromBuffer(await response.Content.ReadAsByteArrayAsync()))
        {
            Assert.Equal((256, 256), (image.Width, image.Height));
        }

        Assert.True(
            File.Exists(Path.Combine(app.DerivedPath, "avatars", $"{session.User.Id:N}.webp"))
        );

        // People lists carry it too.
        var people = await bob.GetFromJsonAsync<List<PersonDto>>(
            "/api/v1/people?q=Alice",
            Json.Options
        );
        Assert.Equal(session.User.Avatar, Assert.Single(people!).Avatar);

        using var anonymous = app.CreateBrowser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (
                await bob.GetAsync(
                    $"/api/v1/people/{(await DokumentApp.GetSessionAsync(bob)).User.Id}/avatar"
                )
            ).StatusCode
        );
    }

    [Fact]
    public async Task Signing_in_again_skips_an_unchanged_picture_and_takes_a_new_one()
    {
        await using var app = await StartAsync();
        app.Idp.Pictures["alice.png"] = TestFiles.Png(400, 300);
        var alice = Alice($"{FakeOidcProvider.Issuer}/pictures/alice.png");
        using var browser = await FileTests.SignInAsync(app, alice);
        var first = (await WaitForAvatarAsync(browser, present: true)).User.Avatar;

        await app.LoginAsync(browser, alice);
        await WaitForJobsAsync(app);
        Assert.Equal(1, app.Idp.PicturesNotModified);
        Assert.Equal(first, (await DokumentApp.GetSessionAsync(browser)).User.Avatar);

        app.Idp.Pictures["alice.png"] = TestFiles.Png(100, 400);
        await app.LoginAsync(browser, alice);
        var second = await WaitForAvatarAsync(browser, present: true, not: first);
        Assert.NotEqual(first, second.User.Avatar);
    }

    [Fact]
    public async Task A_data_url_picture_works_and_goes_when_the_claim_does()
    {
        await using var app = await StartAsync();
        var png = Convert.ToBase64String(TestFiles.Png(64, 64));
        using var browser = await FileTests.SignInAsync(app, Alice($"data:image/png;base64,{png}"));
        var session = await WaitForAvatarAsync(browser, present: true);
        var file = Path.Combine(app.DerivedPath, "avatars", $"{session.User.Id:N}.webp");
        Assert.True(File.Exists(file));

        await app.LoginAsync(browser, Alice(null));
        await WaitForAvatarAsync(browser, present: false);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task A_picture_that_is_not_an_image_leaves_initials()
    {
        await using var app = await StartAsync();
        app.Idp.Pictures["login.html"] = "<html>Sign in</html>"u8.ToArray();
        using var browser = await FileTests.SignInAsync(
            app,
            Alice($"{FakeOidcProvider.Issuer}/pictures/login.html")
        );
        await WaitForJobsAsync(app);

        Assert.Null((await DokumentApp.GetSessionAsync(browser)).User.Avatar);
        Assert.Equal(1, app.Idp.PictureRequests);
    }

    [Fact]
    public async Task Pictures_are_off_when_the_claim_setting_is_empty()
    {
        await using var app = await StartAsync(new() { ["OIDC_PICTURE_CLAIM"] = "" });
        app.Idp.Pictures["alice.png"] = TestFiles.Png(400, 300);
        using var browser = await FileTests.SignInAsync(
            app,
            Alice($"{FakeOidcProvider.Issuer}/pictures/alice.png")
        );
        await WaitForJobsAsync(app);

        Assert.Null((await DokumentApp.GetSessionAsync(browser)).User.Avatar);
        Assert.Equal(0, app.Idp.PictureRequests);
    }

    private static async Task<Dokument.Server.Auth.CurrentSession> WaitForAvatarAsync(
        HttpClient browser,
        bool present,
        string? not = null
    )
    {
        for (var i = 0; i < 100; i++)
        {
            var session = await DokumentApp.GetSessionAsync(browser);
            if (
                (session.User.Avatar is not null) == present
                && (not is null || session.User.Avatar != not)
            )
            {
                return session;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("The picture didn't change.");
    }

    private static async Task WaitForJobsAsync(DokumentApp app)
    {
        for (var i = 0; i < 100; i++)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DokumentDbContext>();
            if (
                !await db.Jobs.AnyAsync(
                    j =>
                        j.Type == JobType.Avatar
                        && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running),
                    TestContext.Current.CancellationToken
                )
            )
            {
                return;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("The picture job didn't finish.");
    }
}
