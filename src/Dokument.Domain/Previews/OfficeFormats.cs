namespace Dokument.Domain.Previews;

/// <summary>The Office and OpenDocument formats Gotenberg (LibreOffice) turns into PDFs (docs/design.md §10.3).</summary>
public static class OfficeFormats
{
    /// <summary>The extension LibreOffice is told, by MIME type. Gotenberg picks the importer from the file name.</summary>
    private static readonly Dictionary<string, string> Extensions = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = "docx",
        ["application/vnd.ms-word.document.macroenabled.12"] = "docm",
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.template"] = "dotx",
        ["application/msword"] = "doc",
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = "xlsx",
        ["application/vnd.ms-excel.sheet.macroenabled.12"] = "xlsm",
        ["application/vnd.ms-excel"] = "xls",
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = "pptx",
        ["application/vnd.ms-powerpoint.presentation.macroenabled.12"] = "pptm",
        ["application/vnd.ms-powerpoint"] = "ppt",
        ["application/vnd.oasis.opendocument.text"] = "odt",
        ["application/vnd.oasis.opendocument.spreadsheet"] = "ods",
        ["application/vnd.oasis.opendocument.presentation"] = "odp",
        ["application/rtf"] = "rtf",
        ["text/rtf"] = "rtf",
    };

    /// <summary>Every MIME type that is previewed through Gotenberg.</summary>
    public static IReadOnlyCollection<string> MimeTypes => Extensions.Keys;

    public static string? ExtensionFor(string? mimeType) =>
        mimeType is not null
        && Extensions.TryGetValue(mimeType.Split(';')[0].Trim(), out var extension)
            ? extension
            : null;

    /// <summary>
    /// Whether the bytes are what the MIME type says: a zip (OOXML, ODF), an OLE compound file (.doc, .xls, .ppt) or
    /// RTF. A renamed text file never reaches LibreOffice.
    /// </summary>
    public static bool LooksLike(string extension, ReadOnlySpan<byte> head) =>
        extension switch
        {
            "doc" or "xls" or "ppt" => head.StartsWith(
                (byte[])[0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]
            ),
            "rtf" => head.StartsWith("{\\rtf"u8),
            _ => head.StartsWith("PK\x03\x04"u8),
        };
}
