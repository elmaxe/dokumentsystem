using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Dokument.Domain.Previews;
using Dokument.Infrastructure.Live;
using Dokument.Server.Auth;
using Dokument.Server.Files;
using Dokument.Server.Live;
using Dokument.Tests;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using static Dokument.IntegrationTests.FileTests;

namespace Dokument.IntegrationTests;

public sealed class LiveTests(PostgresFixture postgres)
{
    private static readonly FakeUser Alice = new(
        "alice-sub",
        "Alice Andersson",
        "alice@example.com"
    );
    private static readonly FakeUser Bob = new("bob-sub", "Bob Berg", "bob@example.com");

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Changes_reach_the_persons_open_clients_and_name_the_tab_that_made_them()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        await using var alice = await LiveClient.SignInAsync(app, Alice);
        await using var bob = await LiveClient.SignInAsync(app, Bob);

        alice.Browser.DefaultRequestHeaders.Add(LiveHub.ClientHeader, "tab-1");
        var folder = await CreateFolderAsync(alice.Browser, null, "Kvitton");
        var created = await alice.NextChangeAsync();
        Assert.Equal(["files"], created.Topics);
        Assert.Equal("tab-1", created.Origin);

        // A change that fails is rolled back, and so is its notice: the next one Alice hears is the rename.
        var clash = await alice.Browser.PostAsJsonAsync(
            "/api/v1/folders",
            new CreateFolderRequest(null, "kvitton")
        );
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        var renamed = await alice.Browser.PostAsJsonAsync(
            $"/api/v1/nodes/{folder.Id}/rename",
            new RenameRequest("Kvitton 2025")
        );
        renamed.EnsureSuccessStatusCode();
        Assert.Equal(["files"], (await alice.NextChangeAsync()).Topics);

        var tree = await alice.Browser.PostAsJsonAsync(
            "/api/v1/folders/tree",
            new FolderTreeRequest(folder.Id, ["2025", "2025/Mars"])
        );
        tree.EnsureSuccessStatusCode();
        Assert.Equal(["files"], (await alice.NextChangeAsync()).Topics);

        var file = await UploadAsync(alice.Browser, "lease.pdf", Bytes(100), folder.Id);
        Assert.Equal(["files", "storage"], (await alice.NextChangeAsync()).Topics);

        await PostIdsAsync(alice.Browser, "/api/v1/nodes/trash", file);
        Assert.Equal(["files", "trash", "storage"], (await alice.NextChangeAsync()).Topics);

        await PostIdsAsync(alice.Browser, "/api/v1/trash/restore", file);
        Assert.Equal(["files", "trash", "storage"], (await alice.NextChangeAsync()).Topics);
        (
            await alice.Browser.PostAsync($"/api/v1/nodes/{file}/opened", null)
        ).EnsureSuccessStatusCode();
        Assert.Equal(["recent"], (await alice.NextChangeAsync()).Topics);

        var preferences = await alice.Browser.PutAsJsonAsync(
            "/api/v1/me/preferences",
            new SetPreferencesRequest(ShowSeconds: true)
        );
        preferences.EnsureSuccessStatusCode();
        Assert.Equal(["account"], (await alice.NextChangeAsync()).Topics);

        // A client id that isn't one is ignored rather than passed on.
        alice.Browser.DefaultRequestHeaders.Remove(LiveHub.ClientHeader);
        alice.Browser.DefaultRequestHeaders.Add(LiveHub.ClientHeader, "<script>");
        await PostIdsAsync(alice.Browser, "/api/v1/nodes/star", folder.Id);
        Assert.Null((await alice.NextChangeAsync()).Origin);

        // Bob heard none of it.
        Assert.False(bob.Changes.Reader.TryRead(out _));
    }

    [Fact]
    public async Task The_worker_tells_clients_when_a_thumbnail_is_ready()
    {
        await using var app = await DokumentApp.StartAsync(
            postgres,
            new Dictionary<string, string?> { ["Worker:PollInterval"] = "00:00:00.100" }
        );
        await using var alice = await LiveClient.SignInAsync(app, Alice);
        alice.Browser.DefaultRequestHeaders.Add(LiveHub.ClientHeader, "tab-1");

        await UploadAsync(alice.Browser, "kvitto.png", TestFiles.Png(400, 300), null);
        Assert.Equal("tab-1", (await alice.NextChangeAsync()).Origin);
        var ready = await alice.NextChangeAsync();
        Assert.Equal(["files"], ready.Topics);
        Assert.Null(ready.Origin);
        Assert.Equal(
            ThumbnailState.Ready,
            (await ListAsync(alice.Browser, null)).Items.Single().Thumbnail
        );
    }

    [Fact]
    public async Task Ending_a_session_signs_its_clients_out_and_closes_their_connections()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        await using var laptop = await LiveClient.SignInAsync(app, Alice);
        await using var phone = await LiveClient.SignInAsync(app, Alice);
        var phoneSession = (await DokumentApp.GetSessionAsync(phone.Browser)).SessionId;

        var revoke = await laptop.Browser.DeleteAsync($"/api/v1/sessions/{phoneSession}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(SessionEndReason.Revoked, await phone.NextSignOutAsync());
        await phone.WaitClosedAsync();
        Assert.False(laptop.SignOuts.Reader.TryRead(out _));
        Assert.Equal(HubConnectionState.Connected, laptop.Hub.State);

        (await laptop.Browser.PostAsync("/api/v1/auth/logout", null)).EnsureSuccessStatusCode();
        Assert.Equal(SessionEndReason.SignedOut, await laptop.NextSignOutAsync());
        await laptop.WaitClosedAsync();
        var connections = app.Services.GetRequiredService<LiveConnections>();
        for (var i = 0; i < 50 && connections.Count > 0; i++)
        {
            await Task.Delay(100);
        }

        Assert.Equal(0, connections.Count);
    }

    [Fact]
    public async Task Only_signed_in_clients_can_connect()
    {
        await using var app = await DokumentApp.StartAsync(postgres);
        var hub = LiveClient.Build(app, new CookieContainerHandler(), csrfToken: null);
        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => hub.StartAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    private static byte[] Bytes(int length) => Enumerable.Repeat((byte)7, length).ToArray();

    private static async Task PostIdsAsync(HttpClient client, string path, params Guid[] ids) =>
        (
            await client.PostAsJsonAsync(path, new NodeIdsRequest([.. ids]))
        ).EnsureSuccessStatusCode();

    /// <summary>A signed-in browser with a live connection, as a web tab has.</summary>
    private sealed class LiveClient : IAsyncDisposable
    {
        public required HttpClient Browser { get; init; }
        public required HubConnection Hub { get; init; }
        public Channel<LiveChangedMessage> Changes { get; } =
            Channel.CreateUnbounded<LiveChangedMessage>();
        public Channel<SessionEndReason> SignOuts { get; } =
            Channel.CreateUnbounded<SessionEndReason>();
        private readonly TaskCompletionSource closed = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public static async Task<LiveClient> SignInAsync(DokumentApp app, FakeUser user)
        {
            var cookies = new CookieContainerHandler();
            var browser = app.CreateDefaultClient(cookies);
            await app.LoginAsync(browser, user);
            var session = await DokumentApp.GetSessionAsync(browser);
            browser.DefaultRequestHeaders.Add(SessionAuthentication.CsrfHeader, session.CsrfToken);

            var client = new LiveClient
            {
                Browser = browser,
                Hub = Build(app, cookies, session.CsrfToken),
            };
            client.Hub.On<LiveChangedMessage>(
                LiveHub.ChangedMethod,
                message => client.Changes.Writer.TryWrite(message)
            );
            client.Hub.On<LiveSignedOutMessage>(
                LiveHub.SignedOutMethod,
                message => client.SignOuts.Writer.TryWrite(message.Reason)
            );
            client.Hub.Closed += _ =>
            {
                client.closed.TrySetResult();
                return Task.CompletedTask;
            };
            await client.Hub.StartAsync();
            return client;
        }

        /// <summary>Long polling, which the in-memory test server can carry; browsers use WebSockets.</summary>
        public static HubConnection Build(
            DokumentApp app,
            CookieContainerHandler cookies,
            string? csrfToken
        ) =>
            new HubConnectionBuilder()
                .WithUrl(
                    new Uri(app.Server.BaseAddress, LiveHub.Path.TrimStart('/')),
                    options =>
                    {
                        options.Transports = HttpTransportType.LongPolling;
                        options.HttpMessageHandlerFactory = _ => new CookieContainerHandler(
                            cookies.Container
                        )
                        {
                            InnerHandler = app.Server.CreateHandler(),
                        };
                        if (csrfToken is not null)
                        {
                            options.Headers[SessionAuthentication.CsrfHeader] = csrfToken;
                        }
                    }
                )
                .AddJsonProtocol(options =>
                    options.PayloadSerializerOptions.Converters.Add(
                        new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
                    )
                )
                .Build();

        public async Task<LiveChangedMessage> NextChangeAsync()
        {
            using var timeout = new CancellationTokenSource(Wait);
            return await Changes.Reader.ReadAsync(timeout.Token);
        }

        public async Task<SessionEndReason> NextSignOutAsync()
        {
            using var timeout = new CancellationTokenSource(Wait);
            return await SignOuts.Reader.ReadAsync(timeout.Token);
        }

        public Task WaitClosedAsync() => closed.Task.WaitAsync(Wait);

        public async ValueTask DisposeAsync()
        {
            await Hub.DisposeAsync();
            Browser.Dispose();
        }
    }
}
