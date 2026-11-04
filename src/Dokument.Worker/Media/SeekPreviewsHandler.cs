using Dokument.Domain.Files;
using Dokument.Domain.Jobs;
using Dokument.Domain.Previews;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Live;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Previews;
using Dokument.Infrastructure.Telemetry;
using Dokument.Worker.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetVips;

namespace Dokument.Worker.Media;

/// <summary>
/// The seek-previews stage (docs/design.md §10.2, §14) for one video blob: one frame for every 1 % of the video (at
/// least 2 s apart), each taken with its own quick ffmpeg seek into the original, joined by NetVips into one WebP sheet
/// that the player shows a part of above its timeline. A frame ffmpeg can't read borrows its neighbour's. A video with
/// no readable frame at all fails the job, so an admin sees it and the backfill doesn't queue it again.
/// </summary>
internal sealed partial class SeekPreviewsHandler(
    DokumentDbContext db,
    BlobStore blobs,
    DerivedStore derived,
    FFmpeg ffmpeg,
    LiveChanges live,
    TimeProvider time,
    ILogger<SeekPreviewsHandler> logger
) : IJobHandler
{
    /// <summary>Lower than thumbnails' 80: the frames are small, many and only glanced at.</summary>
    private const int WebpQuality = 70;

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
        var layout = SeekPreviewLayout.For(media?.DurationSeconds);
        if (media is not { HasVideo: true } || blob is null || layout is null)
        {
            // Purged since, audio, or too short to need them.
            return;
        }

        var (width, height) = SeekPreviewLayout.FrameSize(media.Width, media.Height);
        var source = blobs.PathOf(blob.StorageKey);
        var stopwatch = time.GetTimestamp();
        byte[] sheet;
        using (var activity = DokumentTelemetry.ActivitySource.StartActivity("seek previews"))
        {
            activity?.SetTag("dokument.blob.size", blob.Size);
            activity?.SetTag("dokument.seek_previews.count", layout.Count);
            var frames = new byte[]?[layout.Count];
            for (var i = 0; i < layout.Count; i++)
            {
                frames[i] = await ffmpeg.SeekFrameAsync(
                    source,
                    layout.TimeOf(i),
                    width,
                    height,
                    ct
                );
            }

            if (!Fill(frames))
            {
                throw new InvalidOperationException(
                    "ffmpeg couldn't read any frames of the video."
                );
            }

            sheet = Join(frames!, layout.Columns);
        }

        var key = DerivedKey.For(hash, RenditionKind.SeekPreviews);
        await derived.WriteAsync(key, sheet, ct);
        await db
            .Renditions.Where(r => r.BlobHash == hash && r.Kind == RenditionKind.SeekPreviews)
            .ExecuteDeleteAsync(ct);
        db.Renditions.Add(
            new Rendition
            {
                BlobHash = hash,
                Kind = RenditionKind.SeekPreviews,
                StorageKey = key,
                Width = width * layout.Columns,
                Height = height * layout.Rows,
                CreatedAt = time.GetUtcNow(),
            }
        );
        await db.SaveChangesAsync(ct);
        LogMade(logger, job.Subject, layout.Count, sheet.Length, time.GetElapsedTime(stopwatch));
        DokumentTelemetry.RenditionsGenerated.Add(
            1,
            new KeyValuePair<string, object?>(
                "dokument.rendition.kind",
                RenditionKind.SeekPreviews.Key()
            )
        );

        // An open viewer picks the frames up without being opened again.
        var owners = await db
            .Nodes.Where(n =>
                n.TrashedAt == null && n.CurrentVersion != null && n.CurrentVersion.BlobHash == hash
            )
            .Select(n => n.OwnerId)
            .Distinct()
            .ToListAsync(ct);
        await live.NotifyAsync(owners, LiveTopics.Files, ct);
    }

    /// <summary>
    /// Gives each missing frame the one before it (or after, at the start), so the sheet has no holes. False when
    /// there are no frames at all.
    /// </summary>
    internal static bool Fill(byte[]?[] frames)
    {
        var first = Array.FindIndex(frames, f => f is not null);
        if (first < 0)
        {
            return false;
        }

        for (var i = 0; i < frames.Length; i++)
        {
            frames[i] ??= i < first ? frames[first] : frames[i - 1];
        }

        return true;
    }

    /// <summary>The frames as one WebP, <paramref name="columns"/> to a row, left to right and top to bottom.</summary>
    internal static byte[] Join(IReadOnlyList<byte[]> frames, int columns)
    {
        var images = frames.Select(Load).ToArray();
        try
        {
            using var sheet = Image.Arrayjoin(images, across: columns);
            return sheet.WebpsaveBuffer(q: WebpQuality, keep: Enums.ForeignKeep.None);
        }
        finally
        {
            foreach (var image in images)
            {
                image.Dispose();
            }
        }
    }

    /// <summary>8-bit sRGB without alpha, so every frame joins the same way.</summary>
    private static Image Load(byte[] png)
    {
        using var image = Image.NewFromBuffer(png);
        using var flat = image.HasAlpha() ? image.Flatten() : image.Copy();
        using var srgb = flat.Colourspace(Enums.Interpretation.Srgb);
        using var eight = srgb.Cast(Enums.BandFormat.Uchar);
        // Decoded now, while the PNG bytes are still referenced.
        return eight.CopyMemory();
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Made {Count} seek previews of blob {Hash} ({Bytes} bytes) in {Elapsed}"
    )]
    private static partial void LogMade(
        ILogger logger,
        string hash,
        int count,
        int bytes,
        TimeSpan elapsed
    );
}
