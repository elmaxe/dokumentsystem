using System.Runtime.InteropServices;

namespace Dokument.Worker.Previews;

/// <summary>
/// The few PDFium calls the thumbnailer and the text stage need (docs/design.md §10.3). The native library comes from the
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

    /// <summary>The page's text layer, for search (§10.2). Null when the page has none at all.</summary>
    [LibraryImport(Library)]
    public static partial nint FPDFText_LoadPage(nint page);

    [LibraryImport(Library)]
    public static partial void FPDFText_ClosePage(nint textPage);

    [LibraryImport(Library)]
    public static partial int FPDFText_CountChars(nint textPage);

    /// <summary>Writes <paramref name="count"/> UTF-16 code units and a terminator; returns how many it wrote, the terminator included.</summary>
    [LibraryImport(Library)]
    public static unsafe partial int FPDFText_GetText(
        nint textPage,
        int startIndex,
        int count,
        ushort* result
    );

    /// <summary>For <see cref="FPDF_SaveAsCopy"/>: write the whole file, not an update of the source.</summary>
    public const uint NoIncremental = 2;

    [LibraryImport(Library)]
    public static partial nint FPDF_CreateNewDocument();

    /// <summary>
    /// Copies pages of <paramref name="source"/> into <paramref name="destination"/> before page <paramref name="index"/>
    /// (docs/design.md §8.4). Page indices are 0-based. Nonzero on success.
    /// </summary>
    [LibraryImport(Library)]
    public static unsafe partial int FPDF_ImportPagesByIndex(
        nint destination,
        nint source,
        int* pageIndices,
        CULong length,
        int index
    );

    /// <summary>Writes the document out through <paramref name="writer"/>'s callback. Nonzero on success.</summary>
    [LibraryImport(Library)]
    public static unsafe partial int FPDF_SaveAsCopy(
        nint document,
        FileWrite* writer,
        CULong flags
    );

    /// <summary>
    /// PDFium's <c>FPDF_FILEWRITE</c>, with a handle to the stream after it, since PDFium hands the struct back to the
    /// callback.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct FileWrite
    {
        public int Version;
        public delegate* unmanaged<FileWrite*, void*, CULong, int> WriteBlock;
        public nint Stream;
    }

    /// <summary>Saves a document to <paramref name="stream"/>. Call while holding <see cref="Gate"/>.</summary>
    public static unsafe bool Save(nint document, Stream stream)
    {
        var handle = GCHandle.Alloc(stream);
        try
        {
            var writer = new FileWrite
            {
                Version = 1,
                WriteBlock = &WriteBlock,
                Stream = GCHandle.ToIntPtr(handle),
            };
            return FPDF_SaveAsCopy(document, &writer, new CULong(NoIncremental)) != 0;
        }
        finally
        {
            handle.Free();
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe int WriteBlock(FileWrite* self, void* data, CULong size)
    {
        try
        {
            var stream = (Stream)GCHandle.FromIntPtr(self->Stream).Target!;
            stream.Write(new ReadOnlySpan<byte>(data, checked((int)size.Value)));
            return 1;
        }
        catch (Exception)
        {
            // An exception must not cross into native code; PDFium reports the failed save.
            return 0;
        }
    }
}
