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

namespace Dokument.Worker.Previews;

/// <summary>
/// Page sheets for one PDF blob (docs/design.md §8.4): every page rendered small with PDFium, fitted into a
/// transparent cell of <see cref="PageSheetLayout"/>, and joined by NetVips into WebP sheets of up to 100 pages, which
/// the split screen shows a cell at a time. Made when someone first asks, not for every PDF. A page PDFium can't load
/// stays an empty cell.
/// </summary>
internal sealed partial class PageSheetsHandler(
    DokumentDbContext db,
    BlobStore blobs,
    DerivedStore derived,
    LiveChanges live,
    TimeProvider time,
    ILogger<PageSheetsHandler> logger
) : IJobHandler
{
    private const int WebpQuality = 75;

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

        var stopwatch = time.GetTimestamp();
        int pageCount;
        using (var activity = DokumentTelemetry.ActivitySource.StartActivity("page sheets"))
        {
            pageCount = await MakeAsync(hash, blobs.PathOf(blob.StorageKey), ct);
            activity?.SetTag("dokument.pdf.pages", pageCount);
        }

        await db
            .Renditions.Where(r => r.BlobHash == hash && r.Kind == RenditionKind.PageSheets)
            .ExecuteDeleteAsync(ct);
        db.Renditions.Add(
            new Rendition
            {
                BlobHash = hash,
                Kind = RenditionKind.PageSheets,
                StorageKey = DerivedKey.ForPageSheet(hash, 1),
                Width = PageSheetLayout.CellWidth,
                Height = PageSheetLayout.CellHeight,
                Count = pageCount,
                CreatedAt = time.GetUtcNow(),
            }
        );
        await db.SaveChangesAsync(ct);
        LogMade(logger, job.Subject, pageCount, time.GetElapsedTime(stopwatch));

        var owners = await db
            .Nodes.Where(n =>
                n.TrashedAt == null && n.CurrentVersion != null && n.CurrentVersion.BlobHash == hash
            )
            .Select(n => n.OwnerId)
            .Distinct()
            .ToListAsync(ct);
        await live.NotifyAsync(owners, LiveTopics.Files, ct);
    }

    /// <summary>Writes the sheets and returns the page count.</summary>
    private async Task<int> MakeAsync(ContentHash hash, string path, CancellationToken ct)
    {
        nint document;
        int pageCount;
        lock (Pdfium.Gate)
        {
            Pdfium.EnsureInitialized();
            document = Pdfium.FPDF_LoadDocument(path, password: null);
            if (document == 0)
            {
                throw new InvalidOperationException(
                    $"PDFium couldn't open the file (error {Pdfium.FPDF_GetLastError()})."
                );
            }

            pageCount = Pdfium.FPDF_GetPageCount(document);
        }

        try
        {
            for (var sheet = 0; sheet * PageSheetLayout.PerSheet < pageCount; sheet++)
            {
                var first = sheet * PageSheetLayout.PerSheet;
                var count = Math.Min(PageSheetLayout.PerSheet, pageCount - first);
                var cells = new Image[count];
                try
                {
                    for (var i = 0; i < count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        cells[i] = Cell(document, first + i);
                    }

                    using var joined = Image.Arrayjoin(
                        cells,
                        across: Math.Min(PageSheetLayout.Columns, count)
                    );
                    var bytes = joined.WebpsaveBuffer(q: WebpQuality, keep: Enums.ForeignKeep.None);
                    await derived.WriteAsync(DerivedKey.ForPageSheet(hash, sheet + 1), bytes, ct);
                }
                finally
                {
                    foreach (var cell in cells)
                    {
                        cell?.Dispose();
                    }
                }
            }
        }
        finally
        {
            lock (Pdfium.Gate)
            {
                Pdfium.FPDF_CloseDocument(document);
            }
        }

        return pageCount;
    }

    /// <summary>One page fitted into a transparent cell, centred.</summary>
    private static Image Cell(nint document, int index)
    {
        int width,
            height;
        byte[]? bgrx = null;
        lock (Pdfium.Gate)
        {
            var page = Pdfium.FPDF_LoadPage(document, index);
            (width, height) = (1, 1);
            if (page != 0)
            {
                try
                {
                    var pointWidth = Pdfium.FPDF_GetPageWidthF(page);
                    var pointHeight = Pdfium.FPDF_GetPageHeightF(page);
                    if (pointWidth > 0 && pointHeight > 0)
                    {
                        var scale = Math.Min(
                            PageSheetLayout.CellWidth / pointWidth,
                            PageSheetLayout.CellHeight / pointHeight
                        );
                        width = Math.Clamp(
                            (int)Math.Round(pointWidth * scale),
                            1,
                            PageSheetLayout.CellWidth
                        );
                        height = Math.Clamp(
                            (int)Math.Round(pointHeight * scale),
                            1,
                            PageSheetLayout.CellHeight
                        );
                        bgrx = Thumbnailer.RenderPage(page, width, height);
                    }
                }
                finally
                {
                    Pdfium.FPDF_ClosePage(page);
                }
            }
        }

        Image rgba;
        if (bgrx is null)
        {
            rgba = Image.Black(1, 1, bands: 4);
            (width, height) = (1, 1);
        }
        else
        {
            using var raw = Image.NewFromMemory(
                bgrx,
                width,
                height,
                bands: 4,
                Enums.BandFormat.Uchar
            );
            using var opaque = Image.Black(width, height) + 255;
            using var alpha = opaque.Cast(Enums.BandFormat.Uchar);
            rgba = raw[2]
                .Bandjoin(raw[1], raw[0], alpha)
                .Copy(interpretation: Enums.Interpretation.Srgb);
        }

        using (rgba)
        {
            using var cell = rgba.Embed(
                (PageSheetLayout.CellWidth - width) / 2,
                (PageSheetLayout.CellHeight - height) / 2,
                PageSheetLayout.CellWidth,
                PageSheetLayout.CellHeight,
                extend: Enums.Extend.Background,
                background: [0, 0, 0, 0]
            );
            return cell.CopyMemory();
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Made page sheets of blob {Hash} ({Pages} pages) in {Elapsed}"
    )]
    private static partial void LogMade(ILogger logger, string hash, int pages, TimeSpan elapsed);
}
