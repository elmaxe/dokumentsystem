using System.Runtime.InteropServices;
using Dokument.Domain.Files;
using Dokument.Domain.Jobs;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Previews;
using Dokument.Infrastructure.Telemetry;
using Dokument.Worker.Jobs;
using Microsoft.Extensions.Logging;

namespace Dokument.Worker.Previews;

/// <summary>A source PDF PDFium can't open or copy pages from. The operation fails without retrying.</summary>
internal sealed class PdfUnreadableException(string message) : Exception(message);

/// <summary>
/// Puts the pages of a merge or split together (docs/design.md §8.4): for each new file, PDFium copies the page ranges
/// from the sources into a new document, which is written to the derived volume. The server then adds the files to the
/// drive, since the worker can't write originals (§10.4).
/// </summary>
internal sealed partial class PdfHandler(
    PdfService pdf,
    BlobStore blobs,
    DerivedStore derived,
    ILogger<PdfHandler> logger
) : IJobHandler
{
    public async Task RunAsync(Job job, CancellationToken ct)
    {
        if (!Guid.TryParse(job.Subject, out var id))
        {
            throw new InvalidOperationException($"\"{job.Subject}\" isn't an operation id.");
        }

        var op = await pdf.BeginAsync(id, ct);
        if (op is null)
        {
            // Cancelled, or already done.
            return;
        }

        List<PdfMade> made;
        using (var activity = DokumentTelemetry.ActivitySource.StartActivity("pdf pages"))
        {
            activity?.SetTag("dokument.pdf.kind", op.Kind.ToString().ToLowerInvariant());
            activity?.SetTag("dokument.pdf.pages", op.PagesTotal);
            activity?.SetTag("dokument.pdf.parts", op.Parts.Count);
            try
            {
                made = await MakeAsync(op, ct);
            }
            catch (PdfUnreadableException e)
            {
                LogUnreadable(logger, id, e.Message);
                await pdf.FailAsync(id, e.Message, ct);
                return;
            }
            catch (Exception) when (job.Attempts >= RetryPolicy.Default.MaxAttempts)
            {
                // The last try: the person shouldn't wait for an operation that will never finish.
                await pdf.FailAsync(
                    id,
                    "Something went wrong while putting the pages together.",
                    ct
                );
                throw;
            }
        }

        if (!await pdf.MadeAsync(id, made, ct))
        {
            derived.DeletePdfWork(id);
        }
    }

    private async Task<List<PdfMade>> MakeAsync(PdfOperation op, CancellationToken ct)
    {
        var folder = derived.PdfWorkPath(op.Id);
        Directory.CreateDirectory(folder);
        var documents = new nint[op.Sources.Count];
        var made = new List<PdfMade>();
        var done = 0;
        try
        {
            for (var i = 0; i < op.Parts.Count; i++)
            {
                var part = op.Parts[i];
                nint output;
                lock (Pdfium.Gate)
                {
                    Pdfium.EnsureInitialized();
                    output = Pdfium.FPDF_CreateNewDocument();
                }

                if (output == 0)
                {
                    throw new InvalidOperationException("PDFium couldn't make a new document.");
                }

                try
                {
                    var at = 0;
                    foreach (var range in part.Pages)
                    {
                        ct.ThrowIfCancellationRequested();
                        Import(op, documents, output, range, at);
                        at += range.Count;
                        done += range.Count;
                        await pdf.ProgressAsync(op.Id, done, ct);
                    }

                    var name = $"{i + 1}.pdf";
                    var path = Path.Combine(folder, name);
                    await using (var file = File.Create(path))
                    {
                        bool saved;
                        lock (Pdfium.Gate)
                        {
                            saved = Pdfium.Save(output, file);
                        }

                        if (!saved)
                        {
                            throw new InvalidOperationException(
                                "PDFium couldn't write the new PDF."
                            );
                        }
                    }

                    await using var written = File.OpenRead(path);
                    var hash = await ContentHash.ComputeAsync(written, ct);
                    made.Add(new PdfMade(name, hash.Hex, written.Length));
                }
                finally
                {
                    lock (Pdfium.Gate)
                    {
                        Pdfium.FPDF_CloseDocument(output);
                    }
                }
            }
        }
        finally
        {
            lock (Pdfium.Gate)
            {
                foreach (var document in documents.Where(d => d != 0))
                {
                    Pdfium.FPDF_CloseDocument(document);
                }
            }
        }

        LogMade(logger, op.Id, op.Kind, op.Parts.Count, op.PagesTotal);
        return made;
    }

    /// <summary>Copies one range of a source's pages to the end of <paramref name="output"/>, opening the source the first time.</summary>
    private unsafe void Import(
        PdfOperation op,
        nint[] documents,
        nint output,
        PdfPageRange range,
        int at
    )
    {
        var source = op.Sources[range.Source];
        var indices = Enumerable.Range(range.First - 1, range.Count).ToArray();
        lock (Pdfium.Gate)
        {
            Pdfium.EnsureInitialized();
            if (documents[range.Source] == 0)
            {
                var document = Pdfium.FPDF_LoadDocument(
                    blobs.PathOf(StorageKey.From(source.StorageKey)),
                    password: null
                );
                if (document == 0)
                {
                    throw new PdfUnreadableException(
                        Pdfium.FPDF_GetLastError() == Pdfium.ErrorPassword
                            ? $"“{source.Name}” is locked with a password."
                            : $"“{source.Name}” can't be read. It may be damaged."
                    );
                }

                documents[range.Source] = document;
                if (Pdfium.FPDF_GetPageCount(document) < range.Last)
                {
                    throw new PdfUnreadableException(
                        $"“{source.Name}” has fewer pages than it had when you chose them."
                    );
                }
            }

            fixed (int* pages = indices)
            {
                if (
                    Pdfium.FPDF_ImportPagesByIndex(
                        output,
                        documents[range.Source],
                        pages,
                        new CULong((uint)indices.Length),
                        at
                    ) == 0
                )
                {
                    throw new PdfUnreadableException(
                        $"The pages of “{source.Name}” couldn't be copied."
                    );
                }
            }
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "PDF {Kind} {Id}: made {Parts} files with {Pages} pages"
    )]
    private static partial void LogMade(
        ILogger logger,
        Guid id,
        PdfOperationKind kind,
        int parts,
        int pages
    );

    [LoggerMessage(Level = LogLevel.Information, Message = "PDF operation {Id} failed: {Reason}")]
    private static partial void LogUnreadable(ILogger logger, Guid id, string reason);
}
