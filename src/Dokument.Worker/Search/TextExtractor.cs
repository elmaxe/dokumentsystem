using System.Runtime.InteropServices;
using System.Text;
using Dokument.Domain.Search;
using Dokument.Worker.Previews;
using NetVips;

namespace Dokument.Worker.Search;

/// <summary>One page of text the stage found.</summary>
public sealed record ExtractedPage(
    int PageNo,
    PageSource Source,
    string Text,
    string? Words = null
);

/// <summary>What the text stage made of a file. <see cref="Pages"/> is empty unless <see cref="Status"/> is Ready.</summary>
public sealed record ExtractResult(
    ContentStatus Status,
    int? PageCount,
    IReadOnlyList<ExtractedPage> Pages,
    bool OcrSkipped = false,
    string? OcrEngine = null,
    string? Reason = null,
    bool OcrUnavailable = false
)
{
    public static ExtractResult Unsupported { get; } = new(ContentStatus.Unsupported, null, []);

    public static ExtractResult Failed(string reason) =>
        new(ContentStatus.Failed, null, [], Reason: reason);
}

/// <summary>
/// Finds a file's searchable text (docs/design.md §10.2, §10.3): a PDF's text layer through PDFium, Office and
/// OpenDocument XML, and plain text. Images, and PDF pages with no text layer, go to text recognition when it is on.
/// The format is told by the file's bytes; the stored MIME type only decides whether something is plain text. When
/// Tesseract isn't installed, recognition is skipped as if it were off, so the rest of the text is still kept.
/// </summary>
public sealed class TextExtractor(ITextRecognizer recognizer)
{
    /// <summary>The most text kept per page. A page of dense text is about 5,000 characters.</summary>
    internal const int MaxPageChars = 200_000;

    /// <summary>For files without pages, the most text kept in all.</summary>
    internal const int MaxDocumentChars = 2_000_000;

    /// <summary>The most PDF pages read; a longer file is searchable up to here.</summary>
    internal const int MaxPages = 2000;

    /// <summary>The most pages of one PDF that are sent to text recognition.</summary>
    internal const int MaxOcrPages = 200;

    /// <summary>A page with fewer letters than this in its text layer is taken to be a scan.</summary>
    internal const int MinTextLayerLetters = 16;

    /// <summary>Scans are rendered at 300 dpi for recognition, but never bigger than this on the long edge.</summary>
    private const int OcrMaxEdge = 3500;

    /// <summary>Images smaller than this on both edges, such as icons, aren't recognized.</summary>
    private const int OcrMinEdge = 200;

    private static readonly HashSet<string> ImageLoaders =
    [
        "jpeg",
        "png",
        "spng",
        "webp",
        "gif",
        "nsgif",
        "heif",
        "tiff",
    ];

    /// <summary>MIME types read as text besides <c>text/*</c>.</summary>
    private static readonly HashSet<string> TextTypes =
    [
        "application/json",
        "application/xml",
        "application/javascript",
        "application/x-javascript",
        "application/x-sh",
        "application/x-yaml",
        "application/yaml",
        "application/toml",
        "application/sql",
        "application/rtf",
        "application/x-subrip",
    ];

    public async Task<ExtractResult> ExtractAsync(
        string path,
        string? mimeType,
        bool ocr,
        CancellationToken ct
    )
    {
        var head = Head(path);
        if (head.AsSpan().IndexOf("%PDF-"u8) >= 0)
        {
            return await PdfAsync(path, ocr, ct);
        }

        if (head.AsSpan().StartsWith("PK\x03\x04"u8))
        {
            try
            {
                var pages = await Task.Run(() => OfficeText.Read(path, MaxPageChars, ct), ct);
                return pages is null
                    ? ExtractResult.Unsupported
                    : Result(
                        pages
                            .Select((p, i) => new ExtractedPage(i + 1, PageSource.Text, p))
                            .ToList(),
                        pages.Count > 1 ? pages.Count : null
                    );
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                return ExtractResult.Failed(e.Message);
            }
        }

        if (IsText(mimeType, head))
        {
            var text = await ReadTextAsync(path, ct);
            return Result([new ExtractedPage(1, PageSource.Text, text)], null);
        }

        var loader = ImageLoader(path);
        if (loader is not null && ImageLoaders.Contains(loader))
        {
            return await ImageAsync(path, ocr, ct);
        }

        return ExtractResult.Unsupported;
    }

    private static ExtractResult Result(
        List<ExtractedPage> pages,
        int? pageCount,
        bool ocrSkipped = false,
        string? engine = null
    )
    {
        var kept = pages.Where(p => !string.IsNullOrWhiteSpace(p.Text)).ToList();
        return new ExtractResult(
            kept.Count > 0 ? ContentStatus.Ready : ContentStatus.Empty,
            pageCount,
            kept,
            ocrSkipped,
            kept.Any(p => p.Source == PageSource.Ocr) ? engine : null
        );
    }

    private static byte[] Head(string path)
    {
        var head = new byte[8192];
        using var file = File.OpenRead(path);
        var read = file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return head[..read];
    }

    internal static bool IsText(string? mimeType, ReadOnlySpan<byte> head)
    {
        var type = mimeType?.Split(';')[0].Trim().ToLowerInvariant();
        var textual =
            type is not null
            && (
                type.StartsWith("text/", StringComparison.Ordinal)
                || TextTypes.Contains(type)
                || type.EndsWith("+json", StringComparison.Ordinal)
                || type.EndsWith("+xml", StringComparison.Ordinal)
            );

        // A text type with NUL bytes is really binary; UTF-16 files are the exception, and are rare enough to skip.
        return textual && head.IndexOf((byte)0) < 0;
    }

    /// <summary>UTF-8 (with or without a BOM), falling back to Latin-1 for older files that aren't valid UTF-8.</summary>
    private static async Task<string> ReadTextAsync(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        var bytes = new byte[(int)Math.Min(file.Length, MaxDocumentChars)];
        await file.ReadExactlyAsync(bytes, ct);
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Cut mid-character at the cap, or not UTF-8 at all.
            text = Utf8OrNull(bytes) ?? Encoding.Latin1.GetString(bytes);
        }

        return OfficeText.Tidy(text.TrimStart('﻿').Replace('\0', ' '));
    }

    /// <summary>Decodes as UTF-8 when only the last few bytes are broken, as when the cap cuts a character.</summary>
    private static string? Utf8OrNull(byte[] bytes)
    {
        for (var trim = 1; trim <= 3 && trim < bytes.Length; trim++)
        {
            try
            {
                return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(
                    bytes,
                    0,
                    bytes.Length - trim
                );
            }
            catch (DecoderFallbackException) { }
        }

        return null;
    }

    private static string? ImageLoader(string path)
    {
        try
        {
            return Image
                .FindLoad(path)
                ?.Replace("VipsForeignLoad", "", StringComparison.Ordinal)
                .Replace("File", "", StringComparison.Ordinal)
                .ToLowerInvariant();
        }
        catch (VipsException)
        {
            return null;
        }
    }

    private async Task<ExtractResult> ImageAsync(string path, bool ocr, CancellationToken ct)
    {
        if (!ocr)
        {
            return new ExtractResult(ContentStatus.Empty, null, [], OcrSkipped: true);
        }

        byte[] png,
            inverted;
        int width,
            height,
            pad;
        try
        {
            using var header = Image.NewFromFile(path, access: Enums.Access.Sequential);
            if (Math.Max(header.Width, header.Height) < OcrMinEdge)
            {
                return new ExtractResult(ContentStatus.Empty, null, []);
            }

            // Turned upright from EXIF, in grey, at most OcrMaxEdge: what Tesseract reads best.
            // In memory, because a second look may read it again and the file is read only once, in order.
            using var thumbnail = Image.Thumbnail(
                path,
                OcrMaxEdge,
                height: OcrMaxEdge,
                size: Enums.Size.Down
            );
            using var image = thumbnail.CopyMemory();
            (png, width, height) = GreyPng(image);
            pad = Math.Max(32, Math.Max(width, height) / 20);
            inverted = InvertedPng(image, pad);
        }
        catch (VipsException e)
        {
            return ExtractResult.Failed(e.Message);
        }

        OcrResult result;
        string engine;
        try
        {
            result = await recognizer.RecognizeAsync(png, width, height, ct);
            if (result.Words.Count < TesseractOcr.MinWords)
            {
                // Light text on a dark or busy background, or text up against the edge, is often missed. Tesseract
                // reads it far better dark on white with a margin, so photos get a second look that way.
                var second = await recognizer.RecognizeAsync(
                    inverted,
                    width + (2 * pad),
                    height + (2 * pad),
                    ct
                );
                if (second.Words.Count > result.Words.Count)
                {
                    result = Unpad(second, width, height, pad);
                }
            }

            engine = await recognizer.EngineAsync(ct);
        }
        catch (ToolMissingException)
        {
            return new ExtractResult(
                ContentStatus.Empty,
                null,
                [],
                OcrSkipped: true,
                OcrUnavailable: true
            );
        }

        return Result(
            result.Text.Length > 0
                ? [new ExtractedPage(1, PageSource.Ocr, Cap(result.Text), result.WordsJson())]
                : [],
            null,
            engine: engine
        );
    }

    private static (byte[] Png, int Width, int Height) GreyPng(Image image)
    {
        using var flat = image.HasAlpha() ? image.Flatten(background: [255]) : image.Copy();
        using var grey = flat.Colourspace(Enums.Interpretation.Bw);
        using var eight =
            grey.Format == Enums.BandFormat.Uchar
                ? grey.Copy()
                : grey.Cast(Enums.BandFormat.Uchar, shift: grey.Format == Enums.BandFormat.Ushort);
        return (
            eight.PngsaveBuffer(compression: 1, keep: Enums.ForeignKeep.None),
            eight.Width,
            eight.Height
        );
    }

    /// <summary>The image in grey, inverted and with a white margin of <paramref name="pad"/> pixels.</summary>
    private static byte[] InvertedPng(Image image, int pad)
    {
        using var flat = image.HasAlpha() ? image.Flatten(background: [255]) : image.Copy();
        using var grey = flat.Colourspace(Enums.Interpretation.Bw);
        using var eight =
            grey.Format == Enums.BandFormat.Uchar
                ? grey.Copy()
                : grey.Cast(Enums.BandFormat.Uchar, shift: grey.Format == Enums.BandFormat.Ushort);
        using var inverted = eight.Invert();
        using var padded = inverted.Embed(
            pad,
            pad,
            eight.Width + (2 * pad),
            eight.Height + (2 * pad),
            extend: Enums.Extend.White
        );
        return padded.PngsaveBuffer(compression: 1, keep: Enums.ForeignKeep.None);
    }

    /// <summary>Moves word boxes found on the padded image back onto the original.</summary>
    internal static OcrResult Unpad(OcrResult result, int width, int height, int pad)
    {
        double paddedWidth = width + (2 * pad),
            paddedHeight = height + (2 * pad);
        return result with
        {
            Words = result
                .Words.Select(w => new OcrWord(
                    w.Text,
                    Math.Clamp(((w.X * paddedWidth) - pad) / width, 0, 1),
                    Math.Clamp(((w.Y * paddedHeight) - pad) / height, 0, 1),
                    w.Width * paddedWidth / width,
                    w.Height * paddedHeight / height
                ))
                .ToList(),
        };
    }

    private async Task<ExtractResult> PdfAsync(string path, bool ocr, CancellationToken ct)
    {
        nint document;
        int pageCount;
        lock (Pdfium.Gate)
        {
            Pdfium.EnsureInitialized();
            document = Pdfium.FPDF_LoadDocument(path, password: null);
            if (document == 0)
            {
                return Pdfium.FPDF_GetLastError() == Pdfium.ErrorPassword
                    ? ExtractResult.Failed("The PDF is password-protected.")
                    : ExtractResult.Failed(
                        $"PDFium couldn't open the file (error {Pdfium.FPDF_GetLastError()})."
                    );
            }

            pageCount = Pdfium.FPDF_GetPageCount(document);
        }

        try
        {
            var pages = new List<ExtractedPage>();
            var ocrPages = 0;
            var skipped = false;
            var unavailable = false;
            string? engine = null;
            for (var index = 0; index < Math.Min(pageCount, MaxPages); index++)
            {
                ct.ThrowIfCancellationRequested();
                var (text, scan) = PdfPage(
                    document,
                    index,
                    render: ocr && !unavailable && ocrPages < MaxOcrPages
                );
                if (Letters(text) >= MinTextLayerLetters)
                {
                    pages.Add(new ExtractedPage(index + 1, PageSource.Text, text));
                    continue;
                }

                if (scan is null)
                {
                    // No text layer worth the name, and no recognition for it now.
                    skipped |= !ocr || unavailable;
                    if (text.Length > 0)
                    {
                        pages.Add(new ExtractedPage(index + 1, PageSource.Text, text));
                    }

                    continue;
                }

                ocrPages++;
                OcrResult result;
                try
                {
                    result = await recognizer.RecognizeAsync(
                        scan.Value.Png,
                        scan.Value.Width,
                        scan.Value.Height,
                        ct
                    );
                    engine ??= await recognizer.EngineAsync(ct);
                }
                catch (ToolMissingException)
                {
                    // Keep the text layer of this and later pages; the rest is read once Tesseract is installed.
                    unavailable = skipped = true;
                    if (text.Length > 0)
                    {
                        pages.Add(new ExtractedPage(index + 1, PageSource.Text, text));
                    }

                    continue;
                }

                if (result.Text.Length > 0)
                {
                    pages.Add(
                        new ExtractedPage(
                            index + 1,
                            PageSource.Ocr,
                            Cap(result.Text),
                            result.WordsJson()
                        )
                    );
                }
                else if (text.Length > 0)
                {
                    pages.Add(new ExtractedPage(index + 1, PageSource.Text, text));
                }
            }

            return Result(pages, pageCount, skipped, engine) with
            {
                OcrUnavailable = unavailable,
            };
        }
        finally
        {
            lock (Pdfium.Gate)
            {
                Pdfium.FPDF_CloseDocument(document);
            }
        }
    }

    /// <summary>A page's text layer, and when <paramref name="render"/> is set and the layer is (nearly) empty, the page as a grey PNG for recognition.</summary>
    private static (string Text, (byte[] Png, int Width, int Height)? Scan) PdfPage(
        nint document,
        int index,
        bool render
    )
    {
        byte[] bgrx;
        int width,
            height;
        string text;
        lock (Pdfium.Gate)
        {
            var page = Pdfium.FPDF_LoadPage(document, index);
            if (page == 0)
            {
                return ("", null);
            }

            try
            {
                text = PageText(page);
                if (!render || Letters(text) >= MinTextLayerLetters)
                {
                    return (text, null);
                }

                var pointWidth = Pdfium.FPDF_GetPageWidthF(page);
                var pointHeight = Pdfium.FPDF_GetPageHeightF(page);
                if (pointWidth <= 0 || pointHeight <= 0)
                {
                    return (text, null);
                }

                var scale = Math.Min(300f / 72f, OcrMaxEdge / Math.Max(pointWidth, pointHeight));
                width = Math.Max(1, (int)Math.Round(pointWidth * scale));
                height = Math.Max(1, (int)Math.Round(pointHeight * scale));
                bgrx = Thumbnailer.RenderPage(page, width, height);
            }
            finally
            {
                Pdfium.FPDF_ClosePage(page);
            }
        }

        using var raw = Image.NewFromMemory(bgrx, width, height, bands: 4, Enums.BandFormat.Uchar);
        using var rgb = raw[2]
            .Bandjoin(raw[1], raw[0])
            .Copy(interpretation: Enums.Interpretation.Srgb);
        var (png, w, h) = GreyPng(rgb);
        return (text, (png, w, h));
    }

    /// <summary>Call while holding the PDFium gate.</summary>
    private static unsafe string PageText(nint page)
    {
        var textPage = Pdfium.FPDFText_LoadPage(page);
        if (textPage == 0)
        {
            return "";
        }

        try
        {
            var count = Math.Min(Pdfium.FPDFText_CountChars(textPage), MaxPageChars);
            if (count <= 0)
            {
                return "";
            }

            var buffer = new ushort[count + 1];
            int written;
            fixed (ushort* pointer = buffer)
            {
                written = Pdfium.FPDFText_GetText(textPage, 0, count, pointer);
            }

            var chars = MemoryMarshal.Cast<ushort, char>(
                buffer.AsSpan(0, Math.Max(0, written - 1))
            );

            // PDFium marks soft hyphens and generated line ends with U+FFFE and U+0002.
            return OfficeText.Tidy(
                new string(chars).Replace("￾", "").Replace('\u0002', '-').Replace('\0', ' ')
            );
        }
        finally
        {
            Pdfium.FPDFText_ClosePage(textPage);
        }
    }

    private static int Letters(string text) => text.Count(char.IsLetter);

    private static string Cap(string text) =>
        text.Length > MaxPageChars ? text[..MaxPageChars] : text;
}
