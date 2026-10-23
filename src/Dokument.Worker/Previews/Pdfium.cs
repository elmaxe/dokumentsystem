using System.Runtime.InteropServices;

namespace Dokument.Worker.Previews;

/// <summary>
/// The few PDFium calls the thumbnailer needs (docs/design.md §10.3). The native library comes from the
/// bblanchon.PDFium packages. PDFium isn't thread-safe, so every call goes through <see cref="Gate"/>.
/// </summary>
internal static partial class Pdfium
{
    private const string Library = "pdfium";

    public const uint ErrorPassword = 4;
    public const int RenderAnnotations = 0x01;

    public static readonly Lock Gate = new();

    private static bool initialized;

    /// <summary>Call while holding <see cref="Gate"/>.</summary>
    public static void EnsureInitialized()
    {
        if (!initialized)
        {
            FPDF_InitLibrary();
            initialized = true;
        }
    }

    [LibraryImport(Library)]
    private static partial void FPDF_InitLibrary();

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint FPDF_LoadDocument(string path, string? password);

    [LibraryImport(Library)]
    public static partial uint FPDF_GetLastError();

    [LibraryImport(Library)]
    public static partial void FPDF_CloseDocument(nint document);

    [LibraryImport(Library)]
    public static partial int FPDF_GetPageCount(nint document);

    [LibraryImport(Library)]
    public static partial nint FPDF_LoadPage(nint document, int index);

    [LibraryImport(Library)]
    public static partial void FPDF_ClosePage(nint page);

    /// <summary>In points, with the page's /Rotate applied.</summary>
    [LibraryImport(Library)]
    public static partial float FPDF_GetPageWidthF(nint page);

    [LibraryImport(Library)]
    public static partial float FPDF_GetPageHeightF(nint page);

    /// <summary>A 32-bit BGRx bitmap when <paramref name="alpha"/> is 0.</summary>
    [LibraryImport(Library)]
    public static partial nint FPDFBitmap_Create(int width, int height, int alpha);

    [LibraryImport(Library)]
    public static partial void FPDFBitmap_FillRect(
        nint bitmap,
        int left,
        int top,
        int width,
        int height,
        uint color
    );

    [LibraryImport(Library)]
    public static partial nint FPDFBitmap_GetBuffer(nint bitmap);

    [LibraryImport(Library)]
    public static partial int FPDFBitmap_GetStride(nint bitmap);

    [LibraryImport(Library)]
    public static partial void FPDFBitmap_Destroy(nint bitmap);

    [LibraryImport(Library)]
    public static partial void FPDF_RenderPageBitmap(
        nint bitmap,
        nint page,
        int startX,
        int startY,
        int sizeX,
        int sizeY,
        int rotate,
        int flags
    );
}
