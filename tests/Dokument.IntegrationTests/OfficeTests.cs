using System.Net;
using System.Net.Http.Json;
using Dokument.Domain.Previews;
using Dokument.Infrastructure.Settings;
using Dokument.Server.Admin;
using Dokument.Server.Files;
using Dokument.Tests;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using NetVips;
using static Dokument.IntegrationTests.FileTests;

namespace Dokument.IntegrationTests;

/// <summary>Office previews (docs/design.md §10.3, §14), against a real Gotenberg container.</summary>
public sealed class OfficeTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly FakeUser Admin = new(
        "admin-sub",
        "Anna Admin",
        "anna@example.com",
        "dokument-admins"
    );

    private static readonly Dictionary<string, string?> FastWorker = new()
    {
        ["Worker:PollInterval"] = "00:00:00.100",
    };

    private readonly IContainer gotenberg = new ContainerBuilder("gotenberg/gotenberg:8")
        .WithPortBinding(3000, assignRandomHostPort: true)
        .WithWaitStrategy(
            Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request.ForPath("/health").ForPort(3000))
        )
        .Build();

    public async ValueTask InitializeAsync() => await gotenberg.StartAsync();

    public async ValueTask DisposeAsync() => await gotenberg.DisposeAsync();

    private string GotenbergUrl =>
        $"http://{gotenberg.Hostname}:{gotenberg.GetMappedPublicPort(3000)}";

    [Fact]
    public async Task Office_documents_are_shown_as_PDFs_once_Gotenberg_is_set_up()
    {
        await using var app = await DokumentApp.StartAsync(postgres, FastWorker);
        using var admin = await SignInAsync(app, Admin);

        // Uploaded while Office previews are off: the type icon, nothing to show.
        var contract = await UploadAsync(
            admin,
            "Hyresavtal.docx",
            TestFiles.Docx("Hyresavtal för lägenheten", "Hyran betalas i förskott."),
            null
        );
        var nodes = await WaitAsync(admin, _ => true);
        Assert.Equal(ThumbnailState.None, nodes[contract].Thumbnail);
        Assert.False(nodes[contract].ShownAsPdf);

        var test = await admin.PostAsJsonAsync(
            "/api/v1/admin/office/test",
            new TestOfficeRequest(GotenbergUrl),
            Json.Options
        );
        var probe = await test.Content.ReadFromJsonAsync<ProbeResult>(Json.Options);
        Assert.True(probe!.Reachable, probe.Error);
        Assert.StartsWith("Gotenberg", probe.Version, StringComparison.Ordinal);
        var unreachable = await admin.PostAsJsonAsync(
            "/api/v1/admin/office/test",
            new TestOfficeRequest("http://127.0.0.1:9"),
            Json.Options
        );
        Assert.False(
            (await unreachable.Content.ReadFromJsonAsync<ProbeResult>(Json.Options))!.Reachable
        );

        // Saving the address queues the documents uploaded while it was off.
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await admin.PutAsJsonAsync(
                    "/api/v1/admin/settings/office",
                    new OfficeSettingsDto(GotenbergUrl),
                    Json.Options
                )
            ).StatusCode
        );
        nodes = await WaitAsync(admin, n => n.ShownAsPdf);
        Assert.Equal(ThumbnailState.Ready, nodes[contract].Thumbnail);

        var preview = await admin.GetAsync($"/api/v1/nodes/{contract}/preview");
        Assert.Equal("application/pdf", preview.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith(
            "%PDF-",
            System.Text.Encoding.ASCII.GetString(
                (await preview.Content.ReadAsByteArrayAsync())[..5]
            )
        );

        using (
            var thumbnail = Image.NewFromBuffer(
                await admin.GetByteArrayAsync($"/api/v1/nodes/{contract}/thumbnail")
            )
        )
        {
            Assert.True(thumbnail.Height > thumbnail.Width);
        }

        var settings = await admin.GetFromJsonAsync<AdminSettingsDto>(
            "/api/v1/admin/settings",
            Json.Options
        );
        Assert.Equal(GotenbergUrl, settings!.Office.GotenbergUrl);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await admin.PutAsJsonAsync(
                    "/api/v1/admin/settings/office",
                    new OfficeSettingsDto("ftp://gotenberg"),
                    Json.Options
                )
            ).StatusCode
        );
    }

    private static async Task<Dictionary<Guid, NodeDto>> WaitAsync(
        HttpClient client,
        Func<NodeDto, bool> settled
    )
    {
        // LibreOffice's first conversion is slow while the rest of the suite runs alongside.
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (true)
        {
            var listing = await ListAsync(client, null);
            if (listing.Items.All(i => i.Thumbnail != ThumbnailState.Pending && settled(i)))
            {
                return listing.Items.ToDictionary(i => i.Id);
            }

            Assert.True(DateTime.UtcNow < deadline, "The worker hadn't finished after 120 s.");
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }
    }
}
