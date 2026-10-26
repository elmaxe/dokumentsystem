using System.Runtime.InteropServices;
using Dokument.Domain.Previews;
using NetVips;

namespace Dokument.Worker.Previews;

/// <summary>A WebP the thumbnailer made.</summary>
public sealed record RenderedImage(RenditionKind Kind, byte[] Bytes, int Width, int Height);

/// <summary>What the thumbnailer made of a file. <see cref="Images"/> is empty unless <see cref="Status"/> is Ready.</summary>
public sealed record RenderResult(
    PreviewStatus Status,
    int? Width,
    int? Height,
    int? PageCount,
    IReadOnlyList<RenderedImage> Images,
    string? Reason = null
)
{
    public static RenderResult Unsupported { get; } =
        new(PreviewStatus.Unsupported, null, null, null, []);

    public static RenderResult Failed(string reason) =>
        new(PreviewStatus.Failed, null, null, null, [], reason);
}

/// <summary>
/// Makes thumbnails and previews (docs/design.md §10.3): NetVips (libvips) for images and PDFium for page 1 of a PDF.
/// Videos and audio get theirs from a still ffmpeg makes (<see cref="RenderStill"/>).
/// The format is told by the file's bytes, never its name. Originals are only read; EXIF rotation is applied to the
/// renditions, and their metadata is stripped.
/// </summary>
public sealed class Thumbnailer
{
    /// <summary>libvips loaders we trust with user files. Anything else, such as the CSV or matrix loaders, isn't an image to us.</summary>
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
        "svg",
    ];

    /// <summary>Formats every browser shows natively; the viewer serves those from the original unless they are huge.</summary>
    private static readonly HashSet<string> WebLoaders =
    [
        "jpeg",
        "png",
        "spng",
        "webp",
        "gif",
        "nsgif",
    ];

    /// <summary>Bigger than this on the long edge and the viewer gets a scaled preview instead of the original.</summary>
    private const int LargeImageEdge = 4096;

    /// <summary>A PDF's page 1 is rendered this big on the long edge, then scaled down for each thumbnail.</summary>
    private const int PdfRenderEdge = 1280;

    private const int WebpQuality = 80;

    private static readonly RenditionKind[] Thumbnails =
    [
        RenditionKind.ThumbSmall,
        RenditionKind.ThumbMedium,
    ];

    public RenderResult Render(string path, CancellationToken ct)
    {
        if (IsPdf(path))
        {
            return RenderPdf(path, ct);
        }

        // "VipsForeignLoadJpegFile" → "jpeg".
        var loader = Image
            .FindLoad(path)
            ?.Replace("VipsForeignLoad", "", StringComparison.Ordinal)
            .Replace("File", "", StringComparison.Ordinal)
            .ToLowerInvariant();
        return loader is not null && ImageLoaders.Contains(loader)
            ? RenderImage(path, loader, ct)
            : RenderResult.Unsupported;
    }

    /// <summary>
    /// Thumbnails from a still another tool made, such as a video's poster frame or a song's cover art. The size is the
    /// media's own (a video's picture), not the still's.
    /// </summary>
    public RenderResult RenderStill(byte[] image, int? width, int? height, CancellationToken ct)
    {
        try
        {
            using var still = Image.NewFromBuffer(image);
            var images = new List<RenderedImage>();
            foreach (var kind in Thumbnails)
            {
                ct.ThrowIfCancellationRequested();
                using var thumb = still.ThumbnailImage(
                    kind.MaxEdge(),
                    height: kind.MaxEdge(),
                    size: Enums.Size.Down
                );
                images.Add(Encode(kind, thumb));
            }

            return new RenderResult(
                PreviewStatus.Ready,
                width ?? still.Width,
                height ?? still.Height,
                PageCount: null,
                images
            );
        }
        catch (VipsException e)
        {
            return RenderResult.Failed(e.Message);
        }
    }

    private static bool IsPdf(string path)
    {
        // Readers accept "%PDF-" anywhere in the first KiB.
        Span<byte> head = stackalloc byte[1024];
        using var file = File.OpenRead(path);
        var read = file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return head[..read].IndexOf("%PDF-"u8) >= 0;
    }

    private static RenderResult RenderImage(string path, string loader, CancellationToken ct)
    {
        int width,
            height;
        try
        {
            using var header = Image.NewFromFile(path, access: Enums.Access.Sequential);
            (width, height) = Orientation(header) is >= 5 and <= 8
                ? (header.Height, header.Width)
                : (header.Width, header.Height);
        }
        catch (VipsException e)
        {
            return RenderResult.Failed(e.Message);
        }

        var kinds =
            WebLoaders.Contains(loader) && Math.Max(width, height) <= LargeImageEdge
                ? Thumbnails
                : [.. Thumbnails, RenditionKind.Preview];
        var images = new List<RenderedImage>();
        foreach (var kind in kinds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var thumb = Image.Thumbnail(
                    path,
                    kind.MaxEdge(),
                    height: kind.MaxEdge(),
                    size: Enums.Size.Down
                );
                images.Add(Encode(kind, thumb));
            }
            catch (VipsException e)
            {
                return RenderResult.Failed(e.Message);
            }
        }

        return new RenderResult(PreviewStatus.Ready, width, height, PageCount: null, images);
    }

    /// <summary>The EXIF orientation, 1 to 8; 5 to 8 turn the image on its side.</summary>
    private static int Orientation(Image image) =>
        image.Contains("orientation") && image.Get("orientation") is int value ? value : 1;

    private static RenderResult RenderPdf(string path, CancellationToken ct)
    {
        int pageCount,
            pixelWidth,
            pixelHeight;
        float pointWidth,
            pointHeight;
        byte[] bgrx;
        lock (Pdfium.Gate)
        {
            Pdfium.EnsureInitialized();
            var document = Pdfium.FPDF_LoadDocument(path, password: null);
            if (document == 0)
            {
                return Pdfium.FPDF_GetLastError() == Pdfium.ErrorPassword
                    ? RenderResult.Failed("The PDF is password-protected.")
                    : RenderResult.Failed(
                        $"PDFium couldn't open the file (error {Pdfium.FPDF_GetLastError()})."
                    );
            }

            try
            {
                pageCount = Pdfium.FPDF_GetPageCount(document);
                var page = pageCount > 0 ? Pdfium.FPDF_LoadPage(document, 0) : 0;
                if (page == 0)
                {
                    return RenderResult.Failed("The PDF has no readable first page.");
                }

                try
                {
                    pointWidth = Pdfium.FPDF_GetPageWidthF(page);
                    pointHeight = Pdfium.FPDF_GetPageHeightF(page);
                    if (pointWidth <= 0 || pointHeight <= 0)
                    {
                        return RenderResult.Failed("The PDF's first page has no size.");
                    }

                    var scale = PdfRenderEdge / Math.Max(pointWidth, pointHeight);
                    pixelWidth = Math.Max(1, (int)Math.Round(pointWidth * scale));
                    pixelHeight = Math.Max(1, (int)Math.Round(pointHeight * scale));
                    bgrx = RenderPage(page, pixelWidth, pixelHeight);
                }
                finally
                {
                    Pdfium.FPDF_ClosePage(page);
                }
            }
            finally
            {
                Pdfium.FPDF_CloseDocument(document);
            }
        }

        ct.ThrowIfCancellationRequested();
        using var raw = Image.NewFromMemory(
            bgrx,
            pixelWidth,
            pixelHeight,
            bands: 4,
            Enums.BandFormat.Uchar
        );
        using var rgb = raw[2]
            .Bandjoin(raw[1], raw[0])
            .Copy(interpretation: Enums.Interpretation.Srgb);
        var images = new List<RenderedImage>();
        foreach (var kind in Thumbnails)
        {
            using var thumb = rgb.ThumbnailImage(
                kind.MaxEdge(),
                height: kind.MaxEdge(),
                size: Enums.Size.Down
            );
            images.Add(Encode(kind, thumb));
        }

        return new RenderResult(
            PreviewStatus.Ready,
            (int)Math.Round(pointWidth),
            (int)Math.Round(pointHeight),
            pageCount,
            images
        );
    }

    /// <summary>Renders a page onto white, as a reader would, and copies the pixels out. Call while holding the PDFium gate.</summary>
    internal static byte[] RenderPage(nint page, int width, int height)
    {
        var bitmap = Pdfium.FPDFBitmap_Create(width, height, alpha: 0);
        if (bitmap == 0)
        {
            throw new InvalidOperationException("PDFium couldn't allocate a bitmap.");
        }

        try
        {
            Pdfium.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
            Pdfium.FPDF_RenderPageBitmap(
                bitmap,
                page,
                0,
                0,
                width,
                height,
                rotate: 0,
                Pdfium.RenderAnnotations
            );
            var buffer = Pdfium.FPDFBitmap_GetBuffer(bitmap);
            var stride = Pdfium.FPDFBitmap_GetStride(bitmap);
            var row = width * 4;
            var pixels = new byte[row * height];
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(buffer + (y * stride), pixels, y * row, row);
            }

            return pixels;
        }
        finally
        {
            Pdfium.FPDFBitmap_Destroy(bitmap);
        }
    }

    private static RenderedImage Encode(RenditionKind kind, Image image)
    {
        // CMYK JPEGs, 16-bit PNGs and the like: browsers and WebP want 8-bit sRGB.
        using var srgb = image.Interpretation
            is Enums.Interpretation.Srgb
                or Enums.Interpretation.Bw
            ? image.Copy()
            : image.Colourspace(Enums.Interpretation.Srgb);
        using var eight =
            srgb.Format == Enums.BandFormat.Uchar
                ? srgb.Copy()
                : srgb.Cast(Enums.BandFormat.Uchar, shift: srgb.Format == Enums.BandFormat.Ushort);
        var bytes = eight.WebpsaveBuffer(q: WebpQuality, keep: Enums.ForeignKeep.None);
        return new RenderedImage(kind, bytes, eight.Width, eight.Height);
    }
}
