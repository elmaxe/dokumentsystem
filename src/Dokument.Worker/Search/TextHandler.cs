using Dokument.Domain.Files;
using Dokument.Domain.Jobs;
using Dokument.Domain.Search;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Settings;
using Dokument.Infrastructure.Tags;
using Dokument.Infrastructure.Telemetry;
using Dokument.Worker.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dokument.Worker.Search;

/// <summary>
/// The text stage (docs/design.md §10.2) for one blob: reads its text, recognizing scans and photos when that is on,
/// and replaces the blob's text pages, then runs the keyword rules on it. A file it can't read is recorded as such and not retried; only unexpected errors
/// (I/O, the database, Tesseract crashing) make the job fail and retry. A missing Tesseract counts as recognition being
/// off for that file.
/// </summary>
internal sealed partial class TextHandler(
    DokumentDbContext db,
    BlobStore blobs,
    TextExtractor extractor,
    SettingsStore settings,
    TagRules rules,
    TimeProvider time,
    ILogger<TextHandler> logger
) : IJobHandler
{
    /// <summary>Set once the missing-Tesseract warning is logged, so it isn't repeated for every file.</summary>
    private static int warnedOcrMissing;

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
            // Purged since the job was queued.
            return;
        }

        // Any version with these bytes will do; it only tells plain text from other files.
        var mimeType = await db
            .FileVersions.Where(v => v.BlobHash == hash)
            .OrderByDescending(v => v.UploadedAt)
            .Select(v => v.MimeType)
            .FirstOrDefaultAsync(ct);
        var ocr = (await settings.GetAsync(SettingKeys.TextRecognition, ct)).Enabled;

        ExtractResult result;
        using (var activity = DokumentTelemetry.ActivitySource.StartActivity("text extract"))
        {
            activity?.SetTag("dokument.blob.size", blob.Size);
            result = await extractor.ExtractAsync(blobs.PathOf(blob.StorageKey), mimeType, ocr, ct);
            activity?.SetTag("dokument.text.status", result.Status.ToString().ToLowerInvariant());
            activity?.SetTag("dokument.text.pages", result.Pages.Count);
            activity?.SetTag(
                "dokument.text.ocr_pages",
                result.Pages.Count(p => p.Source == PageSource.Ocr)
            );
        }

        if (result.Status == ContentStatus.Failed)
        {
            LogUnreadable(logger, job.Subject, result.Reason);
        }

        LogRead(
            logger,
            job.Subject,
            result.Status,
            result.Pages.Count,
            result.Pages.Count(p => p.Source == PageSource.Ocr),
            result.OcrSkipped
        );

        if (result.OcrUnavailable && Interlocked.Exchange(ref warnedOcrMissing, 1) == 0)
        {
            LogOcrMissing(logger);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Page 0 is the metadata stage's; everything else is replaced.
        await db
            .ContentPages.Where(p =>
                p.BlobHash == hash && p.VersionId == null && p.PageNo != ContentPage.MetadataPage
            )
            .ExecuteDeleteAsync(ct);
        db.ContentPages.AddRange(
            result.Pages.Select(p => new ContentPage
            {
                BlobHash = hash,
                PageNo = p.PageNo,
                Source = p.Source,
                Text = p.Text,
                Words = p.Words,
            })
        );

        var content = await db.BlobContents.SingleOrDefaultAsync(c => c.BlobHash == hash, ct);
        if (content is null)
        {
            content = new BlobContent { BlobHash = hash, Status = result.Status };
            db.BlobContents.Add(content);
        }

        content.Status = result.Status;
        content.PipelineVersion = BlobContent.CurrentPipelineVersion;
        content.PageCount = result.PageCount;
        content.OcrSkipped = result.OcrSkipped;
        content.OcrEngine = result.OcrEngine;
        content.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // Keyword rules run right after the text stage, on what it stored (§13.4).
        await rules.ApplyToBlobAsync(hash, ct);

        var recognized = result.Pages.Count(p => p.Source == PageSource.Ocr);
        if (recognized > 0)
        {
            DokumentTelemetry.OcrPages.Add(recognized);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Text recognition is on but Tesseract isn't installed or can't be run, so scans and photos are skipped. "
            + "Install it, or set Worker:TesseractCommand to its full path; skipped files are read on the next start"
    )]
    private static partial void LogOcrMissing(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Read blob {Hash}: {Status}, {Pages} pages with text, {OcrPages} of them recognized, skipped recognition: {OcrSkipped}"
    )]
    private static partial void LogRead(
        ILogger logger,
        string hash,
        ContentStatus status,
        int pages,
        int ocrPages,
        bool ocrSkipped
    );

    [LoggerMessage(Level = LogLevel.Information, Message = "No text for blob {Hash}: {Reason}")]
    private static partial void LogUnreadable(ILogger logger, string hash, string? reason);
}
