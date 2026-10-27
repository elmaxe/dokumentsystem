using System.Net;
using Dokument.Domain.Files;
using Dokument.Domain.Jobs;
using Dokument.Domain.Search;
using Dokument.Domain.Settings;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Jobs;
using Dokument.Infrastructure.Live;
using Dokument.Infrastructure.Ml;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Settings;
using Dokument.Infrastructure.Telemetry;
using Dokument.Worker.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dokument.Worker.Media;

/// <summary>
/// The transcribe stage (docs/design.md §11.1) for one video or audio blob: ffmpeg makes 16 kHz mono Opus, the first
/// healthy ML service turns it into timed lines, and those are stored as transcript pages of about a minute as they
/// arrive. A run that stops part way resumes after the last stored page. With no ML service answering, the job waits
/// without using up its attempts; with none set at all, there's nothing to do.
/// </summary>
internal sealed partial class TranscribeHandler(
    DokumentDbContext db,
    BlobStore blobs,
    FFmpeg ffmpeg,
    MlClient ml,
    JobQueue queue,
    SettingsStore settings,
    LiveChanges live,
    TimeProvider time,
    IOptions<WorkerOptions> options,
    ILogger<TranscribeHandler> logger
) : IJobHandler
{
    /// <summary>A service that's down is tried again after this long.</summary>
    internal static readonly TimeSpan RetryUnavailable = TimeSpan.FromMinutes(5);

    /// <summary>The lease while the job makes progress; a worker that dies lets go of it this soon.</summary>
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RenewEvery = TimeSpan.FromMinutes(1);

    /// <summary>Speech under this, after voice detection, counts as none.</summary>
    private const int MinSpeechMs = 3000;

    public async Task RunAsync(Job job, CancellationToken ct)
    {
        if (!ContentHash.TryParse(job.Subject, out var hash))
        {
            throw new InvalidOperationException($"\"{job.Subject}\" isn't a blob hash.");
        }

        var media = await db
            .BlobMedia.AsNoTracking()
            .SingleOrDefaultAsync(m => m.BlobHash == hash, ct);
        var blob = await db
            .Blobs.AsNoTracking()
            .OrderByDescending(b => b.RefCount > 0)
            .FirstOrDefaultAsync(b => b.Hash == hash, ct);
        var wanted = await settings.GetAsync(SettingKeys.Transcription, ct);
        if (
            media is not { HasAudio: true }
            || blob is null
            || !wanted.Enabled
            || (media.HasVideo && !wanted.Videos)
        )
        {
            // Purged since, no sound, or turned off since the job was queued.
            return;
        }

        var transcript = await db.BlobTranscripts.SingleOrDefaultAsync(t => t.BlobHash == hash, ct);
        if (transcript is { Status: not TranscriptStatus.Partial })
        {
            return;
        }

        var connection = await ml.PickAsync(ct);
        if (connection is null)
        {
            if (!await ml.ConfiguredAsync(ct))
            {
                return;
            }

            throw new JobPostponedException("No ML service answers.", RetryUnavailable);
        }

        // Resume where a run stopped, unless the admin has picked another model since.
        var resume =
            transcript is { DoneMs: > 0 }
            && (
                wanted.Model == TranscriptionSettings.AutoModel || transcript.Model == wanted.Model
            );
        if (transcript is null)
        {
            transcript = new BlobTranscript { BlobHash = hash, Status = TranscriptStatus.Partial };
            db.BlobTranscripts.Add(transcript);
        }

        if (!resume)
        {
            await db
                .ContentPages.Where(p => p.BlobHash == hash && p.Source == PageSource.Transcript)
                .ExecuteDeleteAsync(ct);
            transcript.DoneMs = 0;
            transcript.Language = null;
        }

        var nextPage = resume
            ? await db
                .ContentPages.Where(p => p.BlobHash == hash && p.Source == PageSource.Transcript)
                .MaxAsync(p => (int?)p.PageNo, ct) + 1
                ?? 1
            : 1;

        await queue.RenewAsync(job, Lease, ct);
        var audio = Path.Combine(Path.GetTempPath(), $"dokument-transcribe-{Guid.NewGuid():N}.ogg");
        try
        {
            var extract = await ffmpeg.RunAsync(
                AudioArguments(blobs.PathOf(blob.StorageKey), audio),
                options.Value.TranscribeJobTimeout,
                null,
                ct
            );
            if (extract.ExitCode != 0)
            {
                transcript.Status = TranscriptStatus.Failed;
                transcript.Error = string.IsNullOrEmpty(extract.Error)
                    ? $"ffmpeg exited with {extract.ExitCode}."
                    : extract.Error[..Math.Min(extract.Error.Length, 2000)];
                transcript.UpdatedAt = time.GetUtcNow();
                await db.SaveChangesAsync(ct);
                LogUnreadable(logger, job.Subject, transcript.Error);
                return;
            }

            using var activity = DokumentTelemetry.ActivitySource.StartActivity("transcribe");
            activity?.SetTag("dokument.media.duration_s", media.DurationSeconds);
            activity?.SetTag("dokument.transcribe.resume_ms", transcript.DoneMs);
            var started = time.GetTimestamp();
            await TranscribeAsync(job, connection, audio, transcript, wanted.Model, nextPage, ct);
            activity?.SetTag(
                "dokument.transcribe.status",
                transcript.Status.ToString().ToLowerInvariant()
            );
            activity?.SetTag("dokument.transcribe.language", transcript.Language);
            LogDone(
                logger,
                job.Subject,
                transcript.Status,
                transcript.Language,
                time.GetElapsedTime(started)
            );
        }
        finally
        {
            File.Delete(audio);
        }

        await NotifyOwnersAsync(hash, ct);
    }

    private async Task TranscribeAsync(
        Job job,
        MlConnection connection,
        string audio,
        BlobTranscript transcript,
        string model,
        int pageNo,
        CancellationToken ct
    )
    {
        var pager = new TranscriptPager();
        var renewed = time.GetTimestamp();
        var stored = pageNo > 1;
        var request = new TranscribeRequest(model, transcript.Language, transcript.DoneMs);

        async Task StoreAsync(TranscriptPage page)
        {
            db.ContentPages.Add(
                new ContentPage
                {
                    BlobHash = transcript.BlobHash,
                    PageNo = pageNo++,
                    Source = PageSource.Transcript,
                    Text = page.Text,
                    Words = TranscriptLine.Serialize(page.Lines),
                    StartMs = page.StartMs,
                    EndMs = page.EndMs,
                }
            );
            transcript.DoneMs = page.EndMs;
            transcript.UpdatedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
            stored = true;
            if (time.GetElapsedTime(renewed) > RenewEvery)
            {
                renewed = time.GetTimestamp();
                await queue.RenewAsync(job, Lease, ct);
            }
        }

        try
        {
            await foreach (var e in ml.TranscribeAsync(connection, audio, request, ct))
            {
                switch (e)
                {
                    case TranscribeEvent.Detected detected:
                        transcript.Language = detected.Language;
                        transcript.Model = detected.Model;
                        transcript.Engine = detected.Engine;
                        transcript.DurationMs = detected.DurationMs;
                        transcript.Error = null;
                        transcript.Status =
                            transcript.DoneMs == 0 && detected.SpeechMs < MinSpeechMs
                                ? TranscriptStatus.NoSpeech
                                : TranscriptStatus.Partial;
                        transcript.UpdatedAt = time.GetUtcNow();
                        await db.SaveChangesAsync(ct);
                        break;
                    case TranscribeEvent.Segment { Line: var line }
                        when line.EndMs > transcript.DoneMs && line.Text.Length > 0:
                        if (pager.Add(line) is { } page)
                        {
                            await StoreAsync(page);
                        }

                        break;
                    case TranscribeEvent.Done:
                        if (pager.Flush() is { } last)
                        {
                            await StoreAsync(last);
                        }

                        transcript.Status = stored
                            ? TranscriptStatus.Ready
                            : TranscriptStatus.NoSpeech;
                        transcript.DoneMs = transcript.DurationMs ?? transcript.DoneMs;
                        transcript.UpdatedAt = time.GetUtcNow();
                        await db.SaveChangesAsync(ct);
                        return;
                    case TranscribeEvent.Failed failed:
                        throw new InvalidOperationException(
                            $"The ML service failed: {failed.Message}"
                        );
                }
            }
        }
        catch (HttpRequestException e)
            when (e.StatusCode is null or >= HttpStatusCode.InternalServerError)
        {
            // Unreachable, or it fell over mid-file: try again later from the last stored page, attempt not counted.
            ml.Forget(connection);
            throw new JobPostponedException(
                $"The ML service stopped answering: {e.Message}",
                RetryUnavailable
            );
        }
        catch (IOException e) when (!ct.IsCancellationRequested)
        {
            ml.Forget(connection);
            throw new JobPostponedException(
                $"The connection to the ML service broke: {e.Message}",
                RetryUnavailable
            );
        }
    }

    /// <summary>ffmpeg's arguments for what's sent: the first audio stream as 16 kHz mono Opus, about 10 MB an hour.</summary>
    internal static List<string> AudioArguments(string source, string target) =>
        [
            "-i",
            source,
            "-map",
            "0:a:0",
            "-vn",
            "-ac",
            "1",
            "-ar",
            "16000",
            "-c:a",
            "libopus",
            "-b:a",
            "24k",
            "-f",
            "ogg",
            "-y",
            target,
        ];

    /// <summary>Everyone with a file of these bytes can now search what's said in it.</summary>
    private async Task NotifyOwnersAsync(ContentHash hash, CancellationToken ct)
    {
        var owners = await db
            .Nodes.Where(n =>
                n.TrashedAt == null && n.CurrentVersion != null && n.CurrentVersion.BlobHash == hash
            )
            .Select(n => n.OwnerId)
            .Distinct()
            .ToListAsync(ct);
        await live.NotifyAsync(owners, LiveTopics.Files, ct);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Transcribed blob {Hash}: {Status}, language {Language}, in {Elapsed}"
    )]
    private static partial void LogDone(
        ILogger logger,
        string hash,
        TranscriptStatus status,
        string? language,
        TimeSpan elapsed
    );

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "No transcript for blob {Hash}: {Error}"
    )]
    private static partial void LogUnreadable(ILogger logger, string hash, string error);
}
