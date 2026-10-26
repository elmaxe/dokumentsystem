using Dokument.Domain.Files;
using Dokument.Domain.Jobs;
using Dokument.Domain.Previews;
using Dokument.Domain.Settings;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Live;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Previews;
using Dokument.Infrastructure.Settings;
using Dokument.Infrastructure.Telemetry;
using Dokument.Worker.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dokument.Worker.Media;

/// <summary>
/// The transcode stage (docs/design.md §10.2, §14) for one blob: makes the playable copy of a video or audio file that
/// browsers can't play as it is. Streams that already play are copied as they are, so H.264 in a .mov is only
/// repackaged; the rest becomes H.264/AAC in MP4 with the moov atom first, so it starts playing at once. A file ffmpeg
/// can't convert is recorded as failed and not retried; I/O and database errors retry.
/// </summary>
internal sealed partial class TranscodeHandler(
    DokumentDbContext db,
    BlobStore blobs,
    DerivedStore derived,
    FFmpeg ffmpeg,
    SettingsStore settings,
    LiveChanges live,
    TimeProvider time,
    IOptions<WorkerOptions> options,
    ILogger<TranscodeHandler> logger
) : IJobHandler
{
    /// <summary>How often the copy's progress is written. The viewer polls for it; no live notice is sent per step.</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(3);

    public async Task RunAsync(Job job, CancellationToken ct)
    {
        if (!ContentHash.TryParse(job.Subject, out var hash))
        {
            throw new InvalidOperationException($"\"{job.Subject}\" isn't a blob hash.");
        }

        var media = await db.BlobMedia.SingleOrDefaultAsync(m => m.BlobHash == hash, ct);
        var blob = await db
            .Blobs.AsNoTracking()
            .OrderByDescending(b => b.RefCount > 0)
            .FirstOrDefaultAsync(b => b.Hash == hash, ct);
        if (media is null || blob is null || !media.NeedsCopy)
        {
            // Purged since, or nothing to do.
            return;
        }

        var source = blobs.PathOf(blob.StorageKey);
        var probe = await ffmpeg.ProbeAsync(source, ct);
        if (probe is null)
        {
            await FailAsync(media, "ffprobe can't read the file any more.", ct);
            return;
        }

        var videoSettings = await settings.GetAsync(SettingKeys.Videos, ct);
        var key = DerivedKey.For(hash, RenditionKind.Playable);
        var target = derived.PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = $"{target}.{Guid.NewGuid():N}.tmp";

        var stopwatch = time.GetTimestamp();
        (int ExitCode, string Error) run;
        try
        {
            using (var activity = DokumentTelemetry.ActivitySource.StartActivity("transcode"))
            {
                activity?.SetTag("dokument.blob.size", blob.Size);
                activity?.SetTag("dokument.media.plan", media.Plan.ToString().ToLowerInvariant());
                run = await ConvertAsync(
                    media,
                    probe,
                    source,
                    temp,
                    videoSettings.MaxEdge,
                    toneMap: probe.IsHdr,
                    ct
                );
                if (run.ExitCode != 0 && probe.IsHdr)
                {
                    // Tone mapping needs zimg; without it an HDR copy still beats none, if a little washed out.
                    run = await ConvertAsync(
                        media,
                        probe,
                        source,
                        temp,
                        videoSettings.MaxEdge,
                        toneMap: false,
                        ct
                    );
                }

                activity?.SetTag("dokument.transcode.exit_code", run.ExitCode);
            }

            if (run.ExitCode != 0)
            {
                LogFailed(logger, job.Subject, run.Error);
                await FailAsync(
                    media,
                    string.IsNullOrEmpty(run.Error)
                        ? $"ffmpeg exited with {run.ExitCode}."
                        : run.Error,
                    ct
                );
                return;
            }

            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }

        var now = time.GetUtcNow();
        var size = probe.DisplaySize;
        await db
            .Renditions.Where(r => r.BlobHash == hash && r.Kind == RenditionKind.Playable)
            .ExecuteDeleteAsync(ct);
        db.Renditions.Add(
            new Rendition
            {
                BlobHash = hash,
                Kind = RenditionKind.Playable,
                StorageKey = key,
                MimeType = media.HasVideo ? "video/mp4" : "audio/mp4",
                Width = size?.Width ?? 0,
                Height = size?.Height ?? 0,
                CreatedAt = now,
            }
        );
        media.CopyStatus = CopyStatus.Ready;
        media.CopyProgress = null;
        media.CopyError = null;
        media.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        LogMade(logger, job.Subject, media.Plan, time.GetElapsedTime(stopwatch));
        DokumentTelemetry.RenditionsGenerated.Add(
            1,
            new KeyValuePair<string, object?>(
                "dokument.rendition.kind",
                RenditionKind.Playable.Key()
            )
        );
        await NotifyOwnersAsync(hash, ct);
    }

    private async Task<(int ExitCode, string Error)> ConvertAsync(
        BlobMedia media,
        MediaProbe probe,
        string source,
        string temp,
        int? maxEdge,
        bool toneMap,
        CancellationToken ct
    )
    {
        var total = probe.DurationSeconds;
        var percent = -1;
        var written = -1;
        using var timer = new PeriodicTimer(ProgressInterval);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var reporter = Task.Run(
            async () =>
            {
                try
                {
                    while (await timer.WaitForNextTickAsync(stop.Token))
                    {
                        var now = Volatile.Read(ref percent);
                        if (now >= 0 && now != written)
                        {
                            written = now;
                            await db
                                .BlobMedia.Where(m => m.BlobHash == media.BlobHash)
                                .ExecuteUpdateAsync(
                                    s => s.SetProperty(m => m.CopyProgress, now),
                                    stop.Token
                                );
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Done.
                }
            },
            CancellationToken.None
        );

        try
        {
            return await ffmpeg.RunAsync(
                CopyArguments(probe, source, temp, maxEdge, toneMap),
                options.Value.TranscodeJobTimeout,
                done =>
                {
                    if (total is > 0)
                    {
                        Volatile.Write(
                            ref percent,
                            (int)Math.Clamp(done.TotalSeconds / total.Value * 100, 0, 99)
                        );
                    }
                },
                ct
            );
        }
        finally
        {
            await stop.CancelAsync();
            await reporter;
        }
    }

    /// <summary>
    /// ffmpeg's arguments for the copy: the first video and audio streams only (no subtitles, chapters or QuickTime
    /// timecode tracks), each copied when browsers play it and converted when they don't.
    /// </summary>
    internal static List<string> CopyArguments(
        MediaProbe probe,
        string source,
        string target,
        int? maxEdge,
        bool toneMap
    )
    {
        List<string> args = ["-i", source, "-map_metadata", "-1", "-map_chapters", "-1"];
        if (probe.Video is { } video)
        {
            args.AddRange(["-map", "0:V:0"]);
            if (video.Codec is { } codec && MediaPlanner.IsWebVideo(codec, video.PixelFormat))
            {
                args.AddRange(["-c:v", "copy"]);
            }
            else
            {
                List<string> filters = [];
                // Fits a box of maxEdge × maxEdge, so 4K landscape becomes 1920 × 1080 and portrait 1080 × 1920.
                filters.Add(
                    maxEdge is { } edge
                        ? $"scale=w='min(iw,{edge})':h='min(ih,{edge})':force_original_aspect_ratio=decrease:force_divisible_by=2"
                        : "scale=trunc(iw/2)*2:trunc(ih/2)*2"
                );
                if (toneMap)
                {
                    filters.Add(
                        "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv"
                    );
                }

                filters.Add("format=yuv420p");
                args.AddRange([
                    "-vf",
                    string.Join(',', filters),
                    "-c:v",
                    "libx264",
                    "-preset",
                    "veryfast",
                    "-crf",
                    "23",
                    "-profile:v",
                    "high",
                ]);
                if (toneMap)
                {
                    args.AddRange([
                        "-color_primaries",
                        "bt709",
                        "-color_trc",
                        "bt709",
                        "-colorspace",
                        "bt709",
                    ]);
                }
            }
        }

        if (probe.Audio is { } audio)
        {
            args.AddRange(["-map", "0:a:0"]);
            args.AddRange(
                audio.Codec is { } codec && MediaPlanner.IsWebAudio(codec)
                    ? ["-c:a", "copy"]
                    : ["-c:a", "aac", "-b:a", probe.Video is null ? "192k" : "160k", "-ac", "2"]
            );
        }

        args.AddRange(["-movflags", "+faststart", "-f", "mp4", "-y", target]);
        return args;
    }

    private async Task FailAsync(BlobMedia media, string error, CancellationToken ct)
    {
        media.CopyStatus = CopyStatus.Failed;
        media.CopyProgress = null;
        media.CopyError = error.Length > 2000 ? error[^2000..] : error;
        media.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await NotifyOwnersAsync(media.BlobHash, ct);
    }

    /// <summary>Everyone with a file of these bytes sees the copy's progress and can play it once it's ready.</summary>
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
        Message = "Made a playable copy of blob {Hash} ({Plan}) in {Elapsed}"
    )]
    private static partial void LogMade(
        ILogger logger,
        string hash,
        PlaybackPlan plan,
        TimeSpan elapsed
    );

    [LoggerMessage(Level = LogLevel.Warning, Message = "No playable copy of blob {Hash}: {Error}")]
    private static partial void LogFailed(ILogger logger, string hash, string error);
}
