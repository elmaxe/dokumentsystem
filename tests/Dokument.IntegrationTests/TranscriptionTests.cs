using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Dokument.Domain.Jobs;
using Dokument.Domain.Search;
using Dokument.Infrastructure.Ml;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Search;
using Dokument.Infrastructure.Settings;
using Dokument.Server.Admin;
using Dokument.Server.Files;
using Dokument.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Dokument.IntegrationTests.FileTests;

namespace Dokument.IntegrationTests;

/// <summary>Speech to text (docs/design.md §11.1), with the real ffmpeg and a fake ML service.</summary>
public sealed class TranscriptionTests(PostgresFixture postgres)
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

    private static readonly byte[] Memo = TestFiles.Video("memo.m4a", "-vn", "-c:a", "aac");

    [Fact]
    public async Task What_is_said_in_a_recording_is_found_and_plays_from_there()
    {
        var ml = new FakeMl();
        await using var app = await DokumentApp.StartAsync(postgres, FastWorker, ml.Install);
        using var admin = await SignInAsync(app, Admin);
        (
            await admin.PutAsJsonAsync(
                "/api/v1/admin/settings/ml",
                new UpdateMlSettingsRequest([new MlEndpointUpdate("http://ml:3003", "key", false)])
            )
        ).EnsureSuccessStatusCode();
        var memo = await UploadAsync(admin, "Möte med hyresvärden.m4a", Memo, null);
        await WaitForTranscribeAsync(app);

        // Sent as 16 kHz Opus, with the key, in the model the settings name.
        var request = Assert.Single(ml.Requests);
        Assert.Equal("auto", request.Model);
        Assert.Equal("0", request.StartMs);
        Assert.Equal("Bearer key", request.Authorization);
        Assert.StartsWith("OggS", request.AudioHead);

        var results = await admin.GetFromJsonAsync<SearchResponse>(
            "/api/v1/search?q=hyresavtal",
            Json.Options,
            TestContext.Current.CancellationToken
        );
        var hit = Assert.Single(results!.Results);
        Assert.Equal(memo, hit.Node.Id);
        Assert.Equal(MatchSource.Transcript, hit.Source);
        Assert.Equal(75_400, hit.AtMs);
        Assert.Null(hit.PageNo);
        Assert.Contains("hyresavtalet", hit.Snippet!.Text);

        var matches = await admin.GetFromJsonAsync<MatchesResponse>(
            $"/api/v1/nodes/{memo}/matches?q=hyresavtal",
            Json.Options
        );
        var page = Assert.Single(matches!.Pages);
        Assert.Equal(PageSource.Transcript, page.Source);
        Assert.Equal([75_400, 76_900], page.Times);

        var transcript = await admin.GetFromJsonAsync<TranscriptDto>(
            $"/api/v1/nodes/{memo}/transcript",
            Json.Options
        );
        Assert.Equal(TranscriptState.Ready, transcript!.State);
        Assert.Equal("sv", transcript.Language);
        Assert.Equal(
            [
                "Hej och välkommen.",
                "Vi behöver prata om hyran.",
                "Vi måste skriva om hyresavtalet, sa han, hyresavtal alltså.",
            ],
            transcript.Lines.Select(l => l.Text)
        );

        // About a minute per page: the line after the pause starts a new one.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DokumentDbContext>();
            var pages = await db
                .ContentPages.Where(p => p.Source == PageSource.Transcript)
                .OrderBy(p => p.PageNo)
                .ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(
                [(1, 0, 62_000), (2, 75_000, 79_000)],
                pages.Select(p => (p.PageNo, p.StartMs, p.EndMs))
            );
        }

        var captions = await admin.GetAsync($"/api/v1/nodes/{memo}/captions.vtt");
        Assert.Equal("text/vtt", captions.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith(
            "WEBVTT\n\n00:00:00.000 --> 00:00:02.000\nHej och välkommen.",
            await captions.Content.ReadAsStringAsync()
        );

        var progress = await admin.GetFromJsonAsync<TranscriptionProgress>(
            "/api/v1/admin/transcription/progress",
            Json.Options
        );
        Assert.Equal(new TranscriptionProgress(1, 0, 0, 0, 0), progress);
    }

    [Fact]
    public async Task Nothing_is_queued_without_an_ml_service_and_adding_one_queues_the_backlog()
    {
        var ml = new FakeMl();
        await using var app = await DokumentApp.StartAsync(postgres, FastWorker, ml.Install);
        using var admin = await SignInAsync(app, Admin);
        var memo = await UploadAsync(admin, "memo.m4a", Memo, null);
        await WaitForAsync(app, db => db.BlobMedia.AnyAsync());
        await WaitForAsync(
            app,
            async db => (await db.Jobs.ToListAsync()).All(j => j.Status == JobStatus.Done)
        );
        Assert.Equal(
            TranscriptState.None,
            (
                await admin.GetFromJsonAsync<TranscriptDto>(
                    $"/api/v1/nodes/{memo}/transcript",
                    Json.Options
                )
            )!.State
        );

        // Turned off, adding a service queues nothing either; turning it on does.
        (
            await admin.PutAsJsonAsync(
                "/api/v1/admin/settings/transcription",
                new TranscriptionSettingsDto(false, "kb-whisper-small", true, [])
            )
        ).EnsureSuccessStatusCode();
        (
            await admin.PutAsJsonAsync(
                "/api/v1/admin/settings/ml",
                new UpdateMlSettingsRequest([new MlEndpointUpdate("http://ml:3003", null, false)])
            )
        ).EnsureSuccessStatusCode();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DokumentDbContext>();
            Assert.False(
                await db.Jobs.AnyAsync(
                    j => j.Type == JobType.Transcribe,
                    TestContext.Current.CancellationToken
                )
            );
        }

        var bad = await admin.PutAsJsonAsync(
            "/api/v1/admin/settings/transcription",
            new TranscriptionSettingsDto(true, "openai/evil", true, [])
        );
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        (
            await admin.PutAsJsonAsync(
                "/api/v1/admin/settings/transcription",
                new TranscriptionSettingsDto(true, "kb-whisper-small", true, [])
            )
        ).EnsureSuccessStatusCode();
        await WaitForTranscribeAsync(app);
        Assert.Equal("kb-whisper-small", Assert.Single(ml.Requests).Model);
        var settings = await admin.GetFromJsonAsync<AdminSettingsDto>(
            "/api/v1/admin/settings",
            Json.Options
        );
        Assert.Equal("kb-whisper-small", settings!.Transcription.Model);
        Assert.Contains("auto", settings.Transcription.Models);
    }

    [Fact]
    public async Task While_no_ml_service_answers_the_job_waits_without_failing()
    {
        var ml = new FakeMl { Down = true };
        await using var app = await DokumentApp.StartAsync(postgres, FastWorker, ml.Install);
        using var admin = await SignInAsync(app, Admin);
        (
            await admin.PutAsJsonAsync(
                "/api/v1/admin/settings/ml",
                new UpdateMlSettingsRequest([new MlEndpointUpdate("http://ml:3003", null, false)])
            )
        ).EnsureSuccessStatusCode();
        await UploadAsync(admin, "memo.m4a", Memo, null);

        var job = await WaitForAsync(
            app,
            db =>
                db.Jobs.FirstOrDefaultAsync(j =>
                    j.Type == JobType.Transcribe && j.LastError != null
                )
        );
        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Equal(0, job.Attempts);
        Assert.Equal("No ML service answers.", job.LastError);
        Assert.True(job.RunAfter > DateTimeOffset.UtcNow.AddMinutes(4));
        Assert.Empty(ml.Requests);
    }

    private static Task WaitForTranscribeAsync(DokumentApp app) =>
        WaitForAsync(
            app,
            async db =>
            {
                var jobs = await db.Jobs.Where(j => j.Type == JobType.Transcribe).ToListAsync();
                Assert.DoesNotContain(jobs, j => j.Status == JobStatus.Failed);
                return jobs.Count > 0 && jobs.All(j => j.Status == JobStatus.Done);
            }
        );

    private static async Task<T> WaitForAsync<T>(
        DokumentApp app,
        Func<DokumentDbContext, Task<T?>> check
    )
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var result = await check(scope.ServiceProvider.GetRequiredService<DokumentDbContext>());
            if (result is not (null or false))
            {
                return result!;
            }

            Assert.True(DateTime.UtcNow < deadline, "The worker didn't get there in time.");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    private sealed record Sent(
        string Model,
        string StartMs,
        string? Authorization,
        string AudioHead
    );

    /// <summary>
    /// Answers like the ML service: healthy unless <see cref="Down"/>, and every recording says the same three lines,
    /// the last after a pause.
    /// </summary>
    private sealed class FakeMl : HttpMessageHandler
    {
        public bool Down { get; init; }

        public ConcurrentQueue<Sent> Requests { get; } = new();

        IReadOnlyCollection<Sent> RequestsSeen => Requests;

        public void Install(IServiceCollection services)
        {
            services.AddHttpClient<ServiceProbe>().ConfigurePrimaryHttpMessageHandler(() => this);
            services.AddHttpClient<MlClient>().ConfigurePrimaryHttpMessageHandler(() => this);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (Down)
            {
                throw new HttpRequestException("Connection refused");
            }

            if (request.RequestUri!.AbsolutePath == "/health")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(
                        new
                        {
                            status = "ok",
                            version = "fake",
                            device = "cpu",
                        }
                    ),
                };
            }

            var form = await request.Content!.ReadAsMultipartAsync(cancellationToken);
            var audio = form["audio"];
            Requests.Enqueue(
                new Sent(
                    form["model"],
                    form["start_ms"],
                    request.Headers.Authorization?.ToString(),
                    audio[..Math.Min(4, audio.Length)]
                )
            );
            const string body = """
                {"type": "detected", "language": "sv", "language_probability": 0.97, "duration_ms": 80000, "speech_ms": 30000, "model": "small", "engine": "fake 1"}
                {"type": "segment", "start_ms": 0, "end_ms": 2000, "text": "Hej och välkommen.", "words": [[0, 400, "Hej"], [500, 800, "och"], [900, 2000, "välkommen."]]}
                {"type": "segment", "start_ms": 60000, "end_ms": 62000, "text": "Vi behöver prata om hyran.", "words": []}
                {"type": "segment", "start_ms": 75000, "end_ms": 79000, "text": "Vi måste skriva om hyresavtalet, sa han, hyresavtal alltså.", "words": [[75000, 75100, "Vi"], [75400, 76000, "hyresavtalet,"], [76900, 77600, "hyresavtal"]]}
                {"type": "done", "segments": 3}

                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson"),
            };
        }
    }
}

internal static class MultipartReading
{
    /// <summary>The parts of a multipart form, by name, as text (the audio as Latin-1, so its first bytes compare).</summary>
    public static async Task<Dictionary<string, string>> ReadAsMultipartAsync(
        this HttpContent content,
        CancellationToken ct
    )
    {
        var parts = new Dictionary<string, string>();
        var multipart = (MultipartFormDataContent)content;
        foreach (var part in multipart)
        {
            var name = part.Headers.ContentDisposition!.Name!.Trim('"');
            parts[name] =
                name == "audio"
                    ? Encoding.Latin1.GetString(await part.ReadAsByteArrayAsync(ct))
                    : await part.ReadAsStringAsync(ct);
        }

        return parts;
    }
}
