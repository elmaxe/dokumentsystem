using Dokument.Domain.Files;
using Dokument.Domain.Previews;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Previews;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;

namespace Dokument.Server.Files;

/// <param name="Ids">The PDFs, in the order their pages go in: 2 to 100.</param>
/// <param name="FolderId">Where the new file goes; left out for the top of My files.</param>
/// <param name="Name">The new file's name. A taken name gets a number, as uploads do.</param>
/// <param name="TrashOriginals">Move the PDFs to the trash once the new file is saved.</param>
public sealed record MergePdfsRequest(
    List<Guid> Ids,
    Guid? FolderId,
    string Name,
    bool TrashOriginals = false
);

/// <param name="Name">The new file's name.</param>
/// <param name="Pages">Its pages, as "1-3, 5": ranges of the original's pages, in order.</param>
public sealed record SplitPartRequest(string Name, string Pages);

/// <param name="Id">The PDF to split.</param>
/// <param name="Parts">The new files, 1 to 500. Pages may be left out, or used in more than one part.</param>
/// <param name="NewFolderName">Put the new files in a new folder with this name, next to the PDF.</param>
/// <param name="TrashOriginal">Move the PDF to the trash once the new files are saved.</param>
public sealed record SplitPdfRequest(
    Guid Id,
    List<SplitPartRequest> Parts,
    string? NewFolderName = null,
    bool TrashOriginal = false
);

/// <param name="DuplicateOf">A file you already had with exactly the same bytes, which "Remove this copy" can point at.</param>
public sealed record PdfResultDto(Guid NodeId, string Name, Guid? DuplicateOf);

/// <param name="Names">The names the new files were asked for, in order.</param>
/// <param name="Sources">The names of the files it reads.</param>
/// <param name="FolderId">Where the new files go.</param>
/// <param name="Results">The files it made, once done.</param>
/// <param name="Error">Why it failed, in words for people.</param>
public sealed record PdfOperationDto(
    Guid Id,
    PdfOperationKind Kind,
    PdfOperationState State,
    int PagesDone,
    int PagesTotal,
    List<string> Names,
    List<string> Sources,
    Guid? FolderId,
    List<PdfResultDto> Results,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt
)
{
    public static PdfOperationDto From(PdfOperation op) =>
        new(
            op.Id,
            op.Kind,
            op.State,
            op.PagesDone,
            op.PagesTotal,
            op.Parts.Select(p => p.Name).ToList(),
            op.Sources.Select(s => s.Name).ToList(),
            op.FolderId,
            op.Results.Select(r => new PdfResultDto(r.NodeId, r.Name, r.DuplicateOf)).ToList(),
            op.Error,
            op.CreatedAt,
            op.FinishedAt
        );
}

public sealed record PdfOperationListing(List<PdfOperationDto> Items);

/// <summary>
/// Where to find each page's image in a PDF's page sheets: page <c>i</c> (from 0) is on sheet <c>i / perSheet + 1</c>,
/// in cell <c>i % perSheet</c>, <c>columns</c> cells to a row (fewer when the sheet holds fewer pages).
/// </summary>
/// <param name="Ready">False while the worker is making them; ask again in a moment, or wait for a live notice.</param>
public sealed record PageSheetsDto(
    bool Ready,
    int PageCount,
    int Sheets,
    int CellWidth,
    int CellHeight,
    int Columns,
    int PerSheet
);

/// <param name="Pages">Where they are in this file, as first and last page.</param>
public sealed record OriginLineDto(
    Guid SourceNodeId,
    string SourceName,
    int SourceFirst,
    int SourceLast,
    int First,
    int Last,
    OriginSourceState State
);

/// <param name="By">Who merged or split it.</param>
public sealed record FileOriginDto(
    PdfOperationKind Kind,
    DateTimeOffset At,
    string By,
    List<OriginLineDto> Lines
);

/// <summary>Merging and splitting PDFs (docs/design.md §8.4), and the page images the split screen shows.</summary>
public static class PdfEndpoints
{
    public static RouteGroupBuilder MapPdfEndpoints(this RouteGroupBuilder api)
    {
        var pdf = api.MapGroup("").WithTags("PDF").RequireAuthorization();
        pdf.MapPost("/pdf/merge", Merge)
            .WithName("MergePdfs")
            .WithDescription(
                "Merges PDFs into one new file, in the order given. Answers at once; the worker puts the pages together, then the file appears. Follow it with GET /pdf/operations/{id}."
            );
        pdf.MapPost("/pdf/split", Split)
            .WithName("SplitPdf")
            .WithDescription(
                "Splits a PDF into new files, one per part, next to it or in a new folder. Answers at once, like merging."
            );
        pdf.MapGet("/pdf/operations", List)
            .WithName("ListPdfOperations")
            .WithDescription("Your merges and splits from the last week, newest first.");
        pdf.MapGet("/pdf/operations/{id:guid}", Get).WithName("GetPdfOperation");
        pdf.MapDelete("/pdf/operations/{id:guid}", Cancel)
            .WithName("CancelPdfOperation")
            .WithDescription("Stops a merge or split that hasn't saved anything yet.");
        pdf.MapPost("/nodes/{id:guid}/page-sheets", RequestPageSheets)
            .WithName("RequestPageSheets")
            .WithDescription(
                "A PDF's pages as small images, for choosing pages: says where each page is, and asks the worker to make them if they aren't made yet. 409 when the PDF can't be read or isn't processed yet."
            );
        pdf.MapGet("/nodes/{id:guid}/page-sheets/{sheet:int}", GetPageSheet)
            .WithName("GetPageSheet")
            .WithDescription(
                "One WebP sheet of page images, numbered from 1, laid out as RequestPageSheets says. Not audited, like previews."
            );
        pdf.MapGet("/nodes/{id:guid}/origin", GetOrigin)
            .WithName("GetFileOrigin")
            .WithDescription(
                "For a file made by merging or splitting PDFs: what its current version was made from. 204 for other files."
            );
        return api;
    }

    private static async Task<Accepted<PdfOperationDto>> Merge(
        MergePdfsRequest request,
        HttpContext context,
        PdfService pdf,
        CancellationToken ct
    )
    {
        var op = await pdf.StartMergeAsync(
            context.Actor(),
            request.Ids,
            request.FolderId,
            request.Name,
            request.TrashOriginals,
            ct
        );
        return TypedResults.Accepted($"/api/v1/pdf/operations/{op.Id}", PdfOperationDto.From(op));
    }

    private static async Task<Accepted<PdfOperationDto>> Split(
        SplitPdfRequest request,
        HttpContext context,
        PdfService pdf,
        CancellationToken ct
    )
    {
        var op = await pdf.StartSplitAsync(
            context.Actor(),
            request.Id,
            (request.Parts ?? []).Select(p => ((string?)p.Name, (string?)p.Pages)).ToList(),
            request.NewFolderName,
            request.TrashOriginal,
            ct
        );
        return TypedResults.Accepted($"/api/v1/pdf/operations/{op.Id}", PdfOperationDto.From(op));
    }

    private static async Task<Ok<PdfOperationListing>> List(
        HttpContext context,
        PdfService pdf,
        CancellationToken ct
    ) =>
        TypedResults.Ok(
            new PdfOperationListing(
                (await pdf.ListAsync(context.Actor(), ct)).Select(PdfOperationDto.From).ToList()
            )
        );

    private static async Task<Ok<PdfOperationDto>> Get(
        Guid id,
        HttpContext context,
        PdfService pdf,
        CancellationToken ct
    ) => TypedResults.Ok(PdfOperationDto.From(await pdf.GetAsync(context.Actor(), id, ct)));

    private static async Task<Ok<PdfOperationDto>> Cancel(
        Guid id,
        HttpContext context,
        PdfService pdf,
        CancellationToken ct
    ) => TypedResults.Ok(PdfOperationDto.From(await pdf.CancelAsync(context.Actor(), id, ct)));

    private static async Task<Results<Ok<PageSheetsDto>, ProblemHttpResult>> RequestPageSheets(
        Guid id,
        HttpContext context,
        DriveService drive,
        PreviewService previews,
        CancellationToken ct
    )
    {
        var node = await drive.GetFileAsync(context.Actor(), id, ct);
        var hash = node.CurrentVersion!.BlobHash;
        var sheets = await previews.FindAsync(hash, RenditionKind.PageSheets, ct);
        if (sheets?.Rendition.Count is { } pages)
        {
            return TypedResults.Ok(Layout(ready: true, pages));
        }

        var states = await previews.PdfStatesAsync([hash], ct);
        if (
            !states.TryGetValue(hash, out var state)
            || state.PageCount is not { } count
            || !await previews.RequestPageSheetsAsync(hash, ct)
        )
        {
            return TypedResults.Problem(
                "The pages can't be shown: the file isn't a readable PDF, or it's still being processed.",
                statusCode: StatusCodes.Status409Conflict
            );
        }

        return TypedResults.Ok(Layout(ready: false, count));
    }

    private static PageSheetsDto Layout(bool ready, int pages) =>
        new(
            ready,
            pages,
            PageSheetLayout.SheetCount(pages),
            PageSheetLayout.CellWidth,
            PageSheetLayout.CellHeight,
            PageSheetLayout.Columns,
            PageSheetLayout.PerSheet
        );

    private static async Task<Results<PhysicalFileHttpResult, NotFound>> GetPageSheet(
        Guid id,
        int sheet,
        HttpContext context,
        DriveService drive,
        PreviewService previews,
        DerivedStore derived,
        CancellationToken ct
    )
    {
        var node = await drive.GetFileAsync(context.Actor(), id, ct);
        var hash = node.CurrentVersion!.BlobHash;
        var sheets = await previews.FindAsync(hash, RenditionKind.PageSheets, ct);
        if (
            sheets?.Rendition is not { Count: { } pages } rendition
            || sheet < 1
            || sheet > PageSheetLayout.SheetCount(pages)
            || derived.PathOf(DerivedKey.ForPageSheet(hash, sheet)) is not { } path
            || !File.Exists(path)
        )
        {
            return TypedResults.NotFound();
        }

        context.Response.Headers.CacheControl = "private, max-age=86400";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return TypedResults.PhysicalFile(
            path,
            rendition.MimeType,
            lastModified: rendition.CreatedAt,
            entityTag: new EntityTagHeaderValue(
                $"\"{hash.Hex}-pages-{sheet}-{rendition.CreatedAt.ToUnixTimeSeconds()}\""
            )
        );
    }

    private static async Task<Results<Ok<FileOriginDto>, NoContent>> GetOrigin(
        Guid id,
        HttpContext context,
        PdfService pdf,
        CancellationToken ct
    ) =>
        await pdf.OriginAsync(context.Actor(), id, ct) is { } origin
            ? TypedResults.Ok(
                new FileOriginDto(
                    origin.Kind,
                    origin.At,
                    origin.ByName,
                    origin
                        .Lines.Select(l => new OriginLineDto(
                            l.SourceNodeId,
                            l.SourceName,
                            l.SourceFirst,
                            l.SourceLast,
                            l.First,
                            l.Last,
                            l.State
                        ))
                        .ToList()
                )
            )
            : TypedResults.NoContent();
}
