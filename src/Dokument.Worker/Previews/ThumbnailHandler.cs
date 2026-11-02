using Dokument.Domain.Files;
using Dokument.Domain.Jobs;
using Dokument.Domain.Previews;
using Dokument.Domain.Search;
using Dokument.Domain.Settings;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Jobs;
using Dokument.Infrastructure.Live;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Previews;
using Dokument.Infrastructure.Search;
using Dokument.Infrastructure.Settings;
using Dokument.Infrastructure.Telemetry;
using Dokument.Worker.Jobs;
using Dokument.Worker.Media;
using Dokument.Worker.Office;
using Dokument.Worker.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dokument.Worker.Previews;

/// <summary>
/// The thumbnail stage (docs/design.md §10.2) for one blob: renders, writes the WebPs to the derived volume, then
/// records them. Video and audio get a poster frame or their cover art from ffmpeg, what ffprobe found is kept as
/// <see cref="BlobMedia"/>, and a playable copy is queued when browsers can't play the original. Office and
/// OpenDocument files become a PDF through Gotenberg, when an admin has set it up, and get thumbnails of its page 1. A file that isn't an
/// image, a PDF or media, or can't be decoded, is recorded as such and not retried; only unexpected errors (I/O, the
/// database) make the job fail and retry.
/// </summary>
internal sealed partial class ThumbnailHandler(
    DokumentDbContext db,
    BlobStore blobs,
    DerivedStore derived,
    Thumbnailer thumbnailer,
    FFmpeg ffmpeg,
    Gotenberg gotenberg,
    JobQueue jobs,
    TranscriptService transcripts,
    SettingsStore settings,
    OfficeAddress office,
    LiveChanges live,
    TimeProvider time,
    ILogger<ThumbnailHandler> logger
) : IJobHandler
{
    public async Task RunAsync(Job job, CancellationToken ct)
    {
        if (!ContentHash.TryParse(job.Subject, out var hash))
        {
            throw new InvalidOperationException($"\"{job.Subject}\" isn't a blob hash.");
        }

        // Any owner's copy will do: renditions belong to the bytes.
        var blob = await db
            .Blobs.AsNoTracking()
            .OrderByDescending(b => b.RefCount > 0)
            .FirstOrDefaultAsync(b => b.Hash == hash, ct);
        if (blob is null)
        {
            // Purged since the job was queued.
            return;
        }

        RenderResult result;
        BlobMedia? media;
        DerivedKey? document = null;
        byte[]? waveform = null;
        using (var render = DokumentTelemetry.ActivitySource.StartActivity("thumbnail render"))
        {
            render?.SetTag("dokument.blob.size", blob.Size);
            result = await Task.Run(
                () => thumbnailer.Render(blobs.PathOf(blob.StorageKey), ct),
                ct
            );
            BlobMedia? probed = null;
            if (
                result.Status == PreviewStatus.Unsupported
                && await OfficeExtensionAsync(hash, blobs.PathOf(blob.StorageKey), ct)
                    is { } extension
            )
            {
                (result, document) = await RenderOfficeAsync(
                    hash,
                    blobs.PathOf(blob.StorageKey),
                    extension,
                    ct
                );
            }
            else if (result.Status == PreviewStatus.Unsupported)
            {
                RenderResult? fromMedia;
                (fromMedia, probed, waveform) = await RenderMediaAsync(
                    hash,
                    blobs.PathOf(blob.StorageKey),
                    ct
                );
                if (fromMedia is null)
                {
                    // ffmpeg isn't installed: nothing is recorded, so the next worker start queues the file again.
                    return;
                }

                result = fromMedia;
            }

            render?.SetTag("dokument.preview.status", result.Status.ToString().ToLowerInvariant());
            media = probed;
        }

        if (result.Status == PreviewStatus.Failed)
        {
            LogUnreadable(logger, job.Subject, result.Reason);
        }

        var now = time.GetUtcNow();
        var renditions = new List<Rendition>();
        if (document is not null)
        {
            renditions.Add(
                new Rendition
                {
                    BlobHash = hash,
                    Kind = RenditionKind.Document,
                    StorageKey = document,
                    MimeType = "application/pdf",
                    Width = result.Width ?? 0,
                    Height = result.Height ?? 0,
                    CreatedAt = now,
                }
            );
        }

        if (waveform is not null)
        {
            var key = DerivedKey.For(hash, RenditionKind.Waveform);
            await derived.WriteAsync(key, waveform, ct);
            renditions.Add(
                new Rendition
                {
                    BlobHash = hash,
                    Kind = RenditionKind.Waveform,
                    StorageKey = key,
                    MimeType = "application/octet-stream",
                    Width = 0,
                    Height = 0,
                    CreatedAt = now,
                }
            );
        }

        foreach (var image in result.Images)
        {
            var key = DerivedKey.For(hash, image.Kind);
            await derived.WriteAsync(key, image.Bytes, ct);
            renditions.Add(
                new Rendition
                {
                    BlobHash = hash,
                    Kind = image.Kind,
                    StorageKey = key,
                    Width = image.Width,
                    Height = image.Height,
                    CreatedAt = now,
                }
            );
        }

        // The playable copy is the transcode stage's, so making thumbnails again keeps it.
        var old = await db
            .Renditions.Where(r => r.BlobHash == hash && r.Kind != RenditionKind.Playable)
            .ToListAsync(ct);
        db.Renditions.RemoveRange(old);
        db.Renditions.AddRange(renditions);

        var preview = await db.BlobPreviews.SingleOrDefaultAsync(p => p.BlobHash == hash, ct);
        if (preview is null)
        {
            preview = new BlobPreview { BlobHash = hash, Status = result.Status };
            db.BlobPreviews.Add(preview);
        }

        preview.Status = result.Status;
        preview.PipelineVersion = BlobPreview.CurrentPipelineVersion;
        preview.Width = result.Width;
        preview.Height = result.Height;
        preview.PageCount = result.PageCount;
        preview.UpdatedAt = now;
        await SaveMediaAsync(job, media, now, ct);
        if (
            document is not null
            && await db.BlobContents.AnyAsync(
                c => c.BlobHash == hash && c.Status == ContentStatus.Unsupported,
                ct
            )
        )
        {
            // .doc, .xls, .ppt and RTF: the text stage couldn't read them, but it can read the PDF.
            await jobs.EnqueueAsync(JobType.Text, job.Subject, job.Priority, ct);
        }

        await db.SaveChangesAsync(ct);

        // Everyone with a file of these bytes sees its thumbnail appear (§16.4).
        var owners = await db
            .Nodes.Where(n =>
                n.TrashedAt == null && n.CurrentVersion != null && n.CurrentVersion.BlobHash == hash
            )
            .Select(n => n.OwnerId)
            .Distinct()
            .ToListAsync(ct);
        await live.NotifyAsync(owners, LiveTopics.Files, ct);
        foreach (var rendition in renditions)
        {
            DokumentTelemetry.RenditionsGenerated.Add(
                1,
                new KeyValuePair<string, object?>("dokument.rendition.kind", rendition.Kind.Key())
            );
        }

        // Kinds this run didn't make, e.g. a preview the new pipeline no longer needs.
        foreach (var gone in old.Where(o => renditions.All(r => r.Kind != o.Kind)))
        {
            derived.Delete(gone.StorageKey);
        }
    }

    private static int toolMissingLogged;

    /// <summary>The extension to give Gotenberg, for Office and OpenDocument files. Null for anything else.</summary>
    private async Task<string?> OfficeExtensionAsync(
        ContentHash hash,
        string path,
        CancellationToken ct
    )
    {
        // Any version with these bytes will do: they share the type.
        var mimeType = await db
            .FileVersions.Where(v => v.BlobHash == hash)
            .OrderByDescending(v => v.UploadedAt)
            .Select(v => v.MimeType)
            .FirstOrDefaultAsync(ct);
        if (OfficeFormats.ExtensionFor(mimeType) is not { } extension)
        {
            return null;
        }

        var head = new byte[8];
        await using (var file = File.OpenRead(path))
        {
            await file.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
        }

        return OfficeFormats.LooksLike(extension, head) ? extension : null;
    }

    /// <summary>
    /// The PDF of an Office file and thumbnails of its page 1, or Unsupported while Office previews are off. A file
    /// Gotenberg can't read is Failed; Gotenberg being down throws, so the job retries.
    /// </summary>
    private async Task<(RenderResult, DerivedKey?)> RenderOfficeAsync(
        ContentHash hash,
        string path,
        string extension,
        CancellationToken ct
    )
    {
        if ((await office.GetAsync(settings, ct)).GotenbergUrl is not { } url)
        {
            return (RenderResult.Unsupported, null);
        }

        var key = DerivedKey.For(hash, RenditionKind.Document);
        var target = derived.PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = $"{target}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var activity = DokumentTelemetry.ActivitySource.StartActivity("office convert"))
            {
                activity?.SetTag("dokument.office.format", extension);
                await gotenberg.ConvertAsync(url, path, extension, temp, ct);
            }

            var result = await Task.Run(() => thumbnailer.Render(temp, ct), ct);
            if (result.Status != PreviewStatus.Ready)
            {
                return (result, null);
            }

            File.Move(temp, target, overwrite: true);
            return (result, key);
        }
        catch (DocumentUnreadableException e)
        {
            return (RenderResult.Failed(e.Message), null);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>
    /// A poster or cover for video and audio, and what ffprobe found; Unsupported, with no media, for anything else.
    /// Audio also gets its waveform (§14), which is its thumbnail when it has no cover art. Null when ffmpeg isn't
    /// installed.
    /// </summary>
    private async Task<(RenderResult?, BlobMedia?, byte[]?)> RenderMediaAsync(
        ContentHash hash,
        string path,
        CancellationToken ct
    )
    {
        MediaProbe? probe;
        try
        {
            probe = await ffmpeg.ProbeAsync(path, ct);
        }
        catch (ToolMissingException e)
        {
            // Outside the image without ffmpeg: no posters, like a missing exiftool means no metadata (§10.3).
            if (Interlocked.Exchange(ref toolMissingLogged, 1) == 0)
            {
                LogToolMissing(logger, e.Message);
            }

            return (null, null, null);
        }

        if (probe is null)
        {
            return (RenderResult.Unsupported, null, null);
        }

        var size = probe.DisplaySize;
        var media = new BlobMedia
        {
            BlobHash = hash,
            Container = probe.FormatName,
            DurationSeconds = probe.DurationSeconds,
            HasVideo = probe.Video is not null,
            HasAudio = probe.Audio is not null,
            VideoCodec = probe.Video?.Codec,
            AudioCodec = probe.Audio?.Codec,
            Width = size?.Width,
            Height = size?.Height,
            Plan = MediaPlanner.Plan(probe.ForPlanning),
        };

        byte[]? still = null;
        if (probe.Video is not null)
        {
            // About a tenth in, past a black intro, but not minutes into a long film.
            var at = TimeSpan.FromSeconds(Math.Min((probe.DurationSeconds ?? 0) * 0.1, 60));
            still = await ffmpeg.FrameAsync(path, at, ct);
            if (still is null && at > TimeSpan.Zero)
            {
                still = await ffmpeg.FrameAsync(path, TimeSpan.Zero, ct);
            }
        }
        else if (probe.Cover is { } cover)
        {
            still = await ffmpeg.CoverAsync(path, cover.Index, ct);
        }

        // Videos have seek previews instead; a waveform is for listening.
        Waveform? waveform = null;
        if (probe.Video is null && probe.Audio is not null)
        {
            using (DokumentTelemetry.ActivitySource.StartActivity("waveform"))
            {
                waveform = await ffmpeg.WaveformAsync(path, ct);
            }

            if (waveform is { Count: 0 })
            {
                waveform = null;
            }
        }

        media.HasCover = probe.Video is null && still is not null;
        media.HasWaveform = waveform is not null;
        var result =
            still is not null ? thumbnailer.RenderStill(still, size?.Width, size?.Height, ct)
            : probe.Video is not null ? RenderResult.Failed("ffmpeg couldn't read a frame.")
            : waveform is not null ? thumbnailer.RenderWaveform(waveform, ct)
            : RenderResult.Unsupported;
        return (result, media, waveform?.Encode());
    }

    /// <summary>Keeps what ffprobe found and queues a playable copy when one is needed and copies are made in the background.</summary>
    private async Task SaveMediaAsync(
        Job job,
        BlobMedia? media,
        DateTimeOffset now,
        CancellationToken ct
    )
    {
        var hash = media?.BlobHash;
        if (hash is null)
        {
            return;
        }

        var stored = await db.BlobMedia.SingleOrDefaultAsync(m => m.BlobHash == hash, ct);
        if (stored is null)
        {
            stored = media!;
            db.BlobMedia.Add(stored);
        }
        else
        {
            stored.Container = media!.Container;
            stored.DurationSeconds = media.DurationSeconds;
            stored.HasVideo = media.HasVideo;
            stored.HasAudio = media.HasAudio;
            stored.VideoCodec = media.VideoCodec;
            stored.AudioCodec = media.AudioCodec;
            stored.Width = media.Width;
            stored.Height = media.Height;
            stored.Plan = media.Plan;
            stored.HasCover = media.HasCover;
            stored.HasWaveform = media.HasWaveform;
        }

        stored.UpdatedAt = now;
        if (
            stored.NeedsCopy
            && stored.CopyStatus == CopyStatus.None
            && (await settings.GetAsync(SettingKeys.Videos, ct)).Copies == VideoCopies.Background
        )
        {
            await jobs.EnqueueAsync(JobType.Transcode, stored.BlobHash.Hex, job.Priority, ct);
        }

        // Speech to text needs to know there's sound, so it's queued from here (§11.1).
        await transcripts.RequestAsync(stored, JobPriority.Background, ct);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "No video posters or playable copies: {Reason}"
    )]
    private static partial void LogToolMissing(ILogger logger, string reason);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "No thumbnail for blob {Hash}: {Reason}"
    )]
    private static partial void LogUnreadable(ILogger logger, string hash, string? reason);
}
