using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Dokument.Worker.Search;

/// <summary>
/// Text from Office Open XML (DOCX, XLSX, PPTX) and OpenDocument files (docs/design.md §10.3). Both are zip files of
/// XML, so the text is read straight from the XML: no Office install and no extra libraries. Presentations get one page
/// per slide; everything else is one page. Reads are capped, so a zip bomb can't fill memory.
/// </summary>
internal static partial class OfficeText
{
    /// <summary>The most uncompressed XML read from one file, across all its parts.</summary>
    private const long MaxXmlBytes = 64L * 1024 * 1024;

    private static readonly XmlReaderSettings XmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    /// <summary>The pages, or null when the zip isn't a format this reads.</summary>
    public static IReadOnlyList<string>? Read(string path, int maxPageChars, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(path);
        var budget = new Budget(MaxXmlBytes);
        if (zip.GetEntry("word/document.xml") is { } document)
        {
            return [Text(document, budget, maxPageChars, ["t"], ["p", "br", "tab"], ct)];
        }

        var slides = zip
            .Entries.Select(e => (Entry: e, Match: Slide().Match(e.FullName)))
            .Where(s => s.Match.Success)
            .OrderBy(s =>
                int.Parse(
                    s.Match.Groups[1].Value,
                    System.Globalization.CultureInfo.InvariantCulture
                )
            )
            .Select(s => s.Entry)
            .ToList();
        if (slides.Count > 0)
        {
            return slides
                .Select(s => Text(s, budget, maxPageChars, ["t"], ["p", "br"], ct))
                .ToList();
        }

        if (zip.GetEntry("xl/workbook.xml") is { } workbook)
        {
            // Text cells live in the shared strings; cells typed straight in are "inline strings" in each sheet.
            var text = new StringBuilder();
            text.Append(Text(workbook, budget, maxPageChars, [], [], ct, sheetNames: true));
            if (zip.GetEntry("xl/sharedStrings.xml") is { } strings)
            {
                text.Append('\n').Append(Text(strings, budget, maxPageChars, ["t"], ["si"], ct));
            }

            foreach (
                var sheet in zip.Entries.Where(e =>
                    e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal)
                )
            )
            {
                text.Append('\n').Append(Text(sheet, budget, maxPageChars, ["t"], ["is"], ct));
            }

            return [Cap(text.ToString().Trim(), maxPageChars)];
        }

        if (zip.GetEntry("mimetype") is { } mimetype && zip.GetEntry("content.xml") is { } content)
        {
            using var reader = new StreamReader(mimetype.Open());
            if (
                !reader
                    .ReadToEnd()
                    .StartsWith("application/vnd.oasis.opendocument", StringComparison.Ordinal)
            )
            {
                return null;
            }

            // In OpenDocument every text node in the body is text; paragraphs and headings end lines.
            return
            [
                Text(
                    content,
                    budget,
                    maxPageChars,
                    null,
                    ["p", "h", "line-break", "tab", "table-cell"],
                    ct
                ),
            ];
        }

        return null;
    }

    /// <param name="textElements">Elements whose text is kept; null keeps all text.</param>
    /// <param name="breakElements">Elements after which a line (or tab) ends.</param>
    private static string Text(
        ZipArchiveEntry entry,
        Budget budget,
        int maxChars,
        string[]? textElements,
        string[] breakElements,
        CancellationToken ct,
        bool sheetNames = false
    )
    {
        var text = new StringBuilder();
        using var stream = new CappedStream(entry.Open(), budget);
        using var xml = XmlReader.Create(stream, XmlSettings);
        var keep = 0;
        try
        {
            while (xml.Read() && text.Length < maxChars)
            {
                ct.ThrowIfCancellationRequested();
                switch (xml.NodeType)
                {
                    case XmlNodeType.Element:
                        if (
                            sheetNames
                            && xml.LocalName == "sheet"
                            && xml.GetAttribute("name") is { } name
                        )
                        {
                            text.AppendLine(name);
                        }

                        if (textElements?.Contains(xml.LocalName) == true && !xml.IsEmptyElement)
                        {
                            keep++;
                        }

                        if (xml.IsEmptyElement && breakElements.Contains(xml.LocalName))
                        {
                            text.Append(xml.LocalName == "tab" ? '\t' : '\n');
                        }

                        break;
                    case XmlNodeType.EndElement:
                        if (textElements?.Contains(xml.LocalName) == true)
                        {
                            keep = Math.Max(0, keep - 1);
                        }

                        if (breakElements.Contains(xml.LocalName))
                        {
                            text.Append(xml.LocalName is "tab" or "table-cell" ? '\t' : '\n');
                        }

                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace:
                        if (textElements is null || keep > 0)
                        {
                            text.Append(xml.Value);
                        }

                        break;
                }
            }
        }
        catch (XmlException) when (text.Length > 0)
        {
            // Keep what was read before the damage.
        }

        return Cap(Tidy(text.ToString()), maxChars);
    }

    private static string Cap(string text, int maxChars) =>
        text.Length > maxChars ? text[..maxChars] : text;

    /// <summary>Collapses runs of blank lines and trailing spaces.</summary>
    internal static string Tidy(string text) =>
        BlankLines().Replace(text.Replace("\r\n", "\n"), "\n\n").Trim();

    [GeneratedRegex(@"^ppt/slides/slide(\d+)\.xml$")]
    private static partial Regex Slide();

    [GeneratedRegex(@"[ \t]*\n(\s*\n)+")]
    private static partial Regex BlankLines();

    private sealed class Budget(long bytes)
    {
        public long Left { get; set; } = bytes;
    }

    /// <summary>Fails once the file's shared budget of uncompressed bytes is spent.</summary>
    private sealed class CappedStream(Stream inner, Budget budget) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, (int)Math.Min(count, Math.Max(1, budget.Left)));
            budget.Left -= read;
            return budget.Left < 0
                ? throw new InvalidDataException("The document is too large to read.")
                : read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
