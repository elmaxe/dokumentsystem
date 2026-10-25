using System.Text.Json;
using Dokument.Domain.Files;
using Dokument.Domain.Jobs;
using Dokument.Domain.Search;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Telemetry;
using Dokument.Worker.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Dokument.Worker.Search;

/// <summary>
/// The metadata stage (docs/design.md §10.2) for one blob: reads it with exiftool, stores the fields search filters on,
/// and makes the searchable tags page 0 of the blob's content.
/// </summary>
internal sealed class MetadataHandler(
    DokumentDbContext db,
    BlobStore blobs,
    ExifTool exifTool,
    TimeProvider time
) : IJobHandler
{
    public async Task RunAsync(Job job, CancellationToken ct)
    {
        if (!ContentHash.TryParse(job.Subject, out var hash))
        {
            throw new InvalidOperationException($"\"{job.Subject}\" isn't a blob hash.");
        }

        var blob = await db
            .Blobs.AsNoTracking()
            .OrderByDescending(b => b.RefCount > 0)
            .FirstOrDefaultAsync(b => b.Hash == hash, ct);
        if (blob is null)
        {
            return;
        }

        MetadataResult result;
        using (var activity = DokumentTelemetry.ActivitySource.StartActivity("metadata read"))
        {
            activity?.SetTag("dokument.blob.size", blob.Size);
            result = await exifTool.ReadAsync(blobs.PathOf(blob.StorageKey), ct);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db
            .ContentPages.Where(p => p.BlobHash == hash && p.PageNo == ContentPage.MetadataPage)
            .ExecuteDeleteAsync(ct);
        if (result.SearchText.Length > 0)
        {
            db.ContentPages.Add(
                new ContentPage
                {
                    BlobHash = hash,
                    PageNo = ContentPage.MetadataPage,
                    Source = PageSource.Metadata,
                    Text = result.SearchText,
                }
            );
        }

        var metadata = await db.BlobMetadata.SingleOrDefaultAsync(m => m.BlobHash == hash, ct);
        if (metadata is null)
        {
            metadata = new BlobMetadata { BlobHash = hash };
            db.BlobMetadata.Add(metadata);
        }

        metadata.PipelineVersion = BlobMetadata.CurrentPipelineVersion;
        metadata.Readable = result.Readable;
        metadata.TakenAt = result.TakenAt?.ToUniversalTime();
        metadata.Camera = result.Camera;
        metadata.DurationSeconds = result.DurationSeconds;
        metadata.Tags = JsonSerializer.Serialize(result.Tags);
        metadata.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
