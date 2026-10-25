using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dokument.Domain.Previews;
using Dokument.Domain.Settings;
using Dokument.Infrastructure.Previews;
using Dokument.Server.Admin;
using Dokument.Server.Files;
using Dokument.Tests;
using static Dokument.IntegrationTests.FileTests;

namespace Dokument.IntegrationTests;

/// <summary>Video posters and playable copies (docs/design.md §14), with the real ffmpeg.</summary>
public sealed class MediaTests(PostgresFixture postgres)
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

    private static readonly byte[] Avi = TestFiles.Video(
        "old.avi",
        "-c:v",
        "mpeg4",
        "-c:a",
        "pcm_s16le"
    );

    private static readonly byte[] Mp4 = TestFiles.Video(
        "phone.mp4",
        "-c:v",
        "libx264",
        "-pix_fmt",
        "yuv420p",
        "-c:a",
        "aac"
    );

    [Fact]
    public async Task Videos_get_a_poster_and_a_copy_that_plays_when_browsers_cant_play_them()
    {
        await using var app = await DokumentApp.StartAsync(postgres, FastWorker);
        using var admin = await SignInAsync(app, Admin);
        var old = await UploadAsync(admin, "old.avi", Avi, null);
        var phone = await UploadAsync(admin, "phone.mp4", Mp4, null);

        var nodes = await WaitAsync(
            admin,
            n =>
                n.Media is not null
                && n.Media.Copy is not CopyState.Waiting and not CopyState.Making
        );
        Assert.Equal(ThumbnailState.Ready, nodes[old].Thumbnail);
        Assert.Equal(ThumbnailState.Ready, nodes[phone].Thumbnail);
        Assert.InRange(nodes[old].Media!.DurationSeconds ?? 0, 1.9, 2.1);
        Assert.True(nodes[old].Media!.HasAudio);
        Assert.Equal(CopyState.Ready, nodes[old].Media!.Copy);
        Assert.Equal(CopyState.NotNeeded, nodes[phone].Media!.Copy);

        // The viewer gets the copy, seekable; the original is still there for browsers that play more.
        var ranged = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/nodes/{old}/preview");
        ranged.Headers.Range = new RangeHeaderValue(0, 99);
        var copy = await admin.SendAsync(ranged);
        Assert.Equal(HttpStatusCode.PartialContent, copy.StatusCode);
        Assert.Equal("video/mp4", copy.Content.Headers.ContentType!.MediaType);
        var original = await admin.GetAsync($"/api/v1/nodes/{old}/preview?original=true");
        Assert.Equal(Avi, await original.Content.ReadAsByteArrayAsync());
        Assert.Equal(Mp4, await admin.GetByteArrayAsync($"/api/v1/nodes/{phone}/preview"));

        var stats = await admin.GetFromJsonAsync<CopyStats>(
            "/api/v1/admin/videos/copies",
            Json.Options
        );
        Assert.Equal(1, stats!.Count);
        Assert.True(stats.Bytes > 0);

        // Copies made when opened: deleting them leaves none until someone asks.
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await admin.PutAsJsonAsync(
                    "/api/v1/admin/settings/videos",
                    new VideoSettingsDto(VideoCopies.OnOpen, 720),
                    Json.Options
                )
            ).StatusCode
        );
        var deleted = await (
            await admin.DeleteAsync("/api/v1/admin/videos/copies")
        ).Content.ReadFromJsonAsync<CopyStats>(Json.Options);
        Assert.Equal(new CopyStats(0, 0, 0, 0), deleted);
        var details = await admin.GetFromJsonAsync<NodeDetails>(
            $"/api/v1/nodes/{old}",
            Json.Options
        );
        Assert.Equal(CopyState.None, details!.Node.Media!.Copy);
        Assert.Equal(
            "video/x-msvideo",
            (await admin.GetAsync($"/api/v1/nodes/{old}/preview"))
                .Content
                .Headers
                .ContentType!
                .MediaType
        );

        var requested = await admin.PostAsync($"/api/v1/nodes/{old}/playable", null);
        Assert.Equal(HttpStatusCode.OK, requested.StatusCode);
        Assert.Contains(
            (await requested.Content.ReadFromJsonAsync<NodeDto>(Json.Options))!.Media!.Copy,
            new[] { CopyState.Waiting, CopyState.Making, CopyState.Ready }
        );
        nodes = await WaitAsync(admin, n => n.Id != old || n.Media!.Copy == CopyState.Ready);
        Assert.Equal(CopyState.Ready, nodes[old].Media!.Copy);

        // Nothing to make for a file that plays, and nothing at all when copies are off.
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await admin.PostAsync($"/api/v1/nodes/{phone}/playable", null)).StatusCode
        );
        await admin.PutAsJsonAsync(
            "/api/v1/admin/settings/videos",
            new VideoSettingsDto(VideoCopies.Never, 1080),
            Json.Options
        );
        await admin.DeleteAsync("/api/v1/admin/videos/copies");
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await admin.PostAsync($"/api/v1/nodes/{old}/playable", null)).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await admin.PutAsJsonAsync(
                    "/api/v1/admin/settings/videos",
                    new VideoSettingsDto(VideoCopies.Background, 480),
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
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var listing = await ListAsync(client, null);
            if (listing.Items.All(i => i.Thumbnail != ThumbnailState.Pending && settled(i)))
            {
                return listing.Items.ToDictionary(i => i.Id);
            }

            Assert.True(DateTime.UtcNow < deadline, "The worker hadn't finished after 60 s.");
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }
    }
}
