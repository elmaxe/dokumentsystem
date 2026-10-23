using Dokument.Domain.Files;
using Dokument.Domain.Jobs;
using Dokument.Domain.Previews;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Previews;
using Dokument.Worker.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dokument.Worker.Previews;

/// <summary>
/// The thumbnail stage (docs/design.md §10.2) for one blob: renders, writes the WebPs to the derived volume, then
/// records them. A file that isn't an image or a PDF, or can't be decoded, is recorded as such and not retried; only
/// unexpected errors (I/O, the database) make the job fail and retry.
/// </summary>
internal sealed partial class ThumbnailHandler(
    DokumentDbContext db, BlobStore blobs, DerivedStore derived, Thumbnailer thumbnailer, TimeProvider time, ILogger<ThumbnailHandler> logger) : IJobHandler
{
    public async Task RunAsync(Job job, CancellationToken ct)
    {
        if (!ContentHash.TryParse(job.Subject, out var hash))
        {
            throw new InvalidOperationException($"\"{job.Subject}\" isn't a blob hash.");
        }

        var blob = await db.Blobs.AsNoTracking().SingleOrDefaultAsync(b => b.Hash == hash, ct);
        if (blob is null)
        {
            // Purged since the job was queued.
            return;
        }

        var result = await Task.Run(() => thumbnailer.Render(blobs.PathOf(blob.StorageKey), ct), ct);
        if (result.Status == PreviewStatus.Failed)
        {
            LogUnreadable(logger, job.Subject, result.Reason);
        }

        var now = time.GetUtcNow();
        var renditions = new List<Rendition>();
        foreach (var image in result.Images)
        {
            var key = DerivedKey.For(hash, image.Kind);
            await derived.WriteAsync(key, image.Bytes, ct);
            renditions.Add(new Rendition { BlobHash = hash, Kind = image.Kind, StorageKey = key, Width = image.Width, Height = image.Height, CreatedAt = now });
        }

        var old = await db.Renditions.Where(r => r.BlobHash == hash).ToListAsync(ct);
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
        await db.SaveChangesAsync(ct);

        // Kinds this run didn't make, e.g. a preview the new pipeline no longer needs.
        foreach (var gone in old.Where(o => renditions.All(r => r.Kind != o.Kind)))
        {
            derived.Delete(gone.StorageKey);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "No thumbnail for blob {Hash}: {Reason}")]
    private static partial void LogUnreadable(ILogger logger, string hash, string? reason);
}
