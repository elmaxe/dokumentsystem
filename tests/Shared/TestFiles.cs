using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using NetVips;

namespace Dokument.Tests;

/// <summary>Small real files for the preview pipeline, made on the fly so the repo holds no binaries.</summary>
internal static class TestFiles
{
    /// <summary>A gradient, so a wrong rotation or channel order shows up in the pixels.</summary>
    public static Image Gradient(int width, int height)
    {
        using var xyz = Image.Xyz(width, height);
        using var red = xyz[0] * (255.0 / width);
        using var green = xyz[1] * (255.0 / height);
        using var blue = (xyz[0] * 0) + 64;
        return red.Bandjoin(green, blue)
            .Cast(Enums.BandFormat.Uchar)
            .Copy(interpretation: Enums.Interpretation.Srgb);
    }

    public static byte[] Png(int width, int height)
    {
        using var image = Gradient(width, height);
        return image.PngsaveBuffer();
    }

    /// <summary>A JPEG whose pixels are stored sideways, with EXIF orientation 6 ("rotate 90° clockwise to view").</summary>
    public static byte[] SidewaysJpeg(int storedWidth, int storedHeight)
    {
        using var image = Gradient(storedWidth, storedHeight)
            .Mutate(m => m.Set(GValue.GIntType, "orientation", 6));
        return image.JpegsaveBuffer(q: 90);
    }

    /// <summary>A valid PDF of <paramref name="pages"/> A4 pages, each with a black box in the top left corner.</summary>
    public static byte[] Pdf(int pages = 1)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(0, pages).Select(i => $"{3 + i} 0 R"))}] /Count {pages} >>",
        };
        var content = 3 + pages;
        for (var i = 0; i < pages; i++)
        {
            objects.Add(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents {content} 0 R >>"
            );
        }

        const string stream = "0 0 0 rg 50 692 200 100 re f";
        objects.Add($"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream");

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append($"{offset:D10} 00000 n \n");
        }

        pdf.Append(
            $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"
        );
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    /// <summary>A PDF with a real text layer: one page per string, in Helvetica (WinAnsi, so å, ä and ö work).</summary>
    public static byte[] TextPdf(params string[] pages)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(0, pages.Length).Select(i => $"{4 + (2 * i)} 0 R"))}] /Count {pages.Length} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        foreach (var text in pages)
        {
            var lines = text.Split('\n')
                .Select(
                    (line, i) => $"BT /F1 14 Tf 72 {760 - (i * 20)} Td ({PdfString(line)}) Tj ET"
                );
            var stream = string.Join("\n", lines);
            objects.Add(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {objects.Count + 2} 0 R >>"
            );
            objects.Add(
                $"<< /Length {Encoding.Latin1.GetByteCount(stream)} >>\nstream\n{stream}\nendstream"
            );
        }

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append($"{offset:D10} 00000 n \n");
        }

        pdf.Append(
            $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"
        );
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }

    private static string PdfString(string text) =>
        text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    /// <summary>Black text on white, big enough for text recognition, as a PNG: a stand-in for a scanned receipt.</summary>
    public static byte[] TextImage(string text)
    {
        using var rendered = Image.Text(text, font: "DejaVu Sans 40", dpi: 150);
        using var inverted = rendered.Invert();
        using var padded = inverted.Embed(
            60,
            60,
            inverted.Width + 120,
            inverted.Height + 120,
            extend: Enums.Extend.White
        );
        return padded.PngsaveBuffer();
    }

    /// <summary>A minimal Word document with one paragraph per string.</summary>
    public static byte[] Docx(params string[] paragraphs) =>
        Zip(
            (
                "[Content_Types].xml",
                "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"
            ),
            (
                "word/document.xml",
                "<?xml version=\"1.0\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>"
                    + string.Concat(
                        paragraphs.Select(p =>
                            $"<w:p><w:r><w:t>{System.Security.SecurityElement.Escape(p)}</w:t></w:r></w:p>"
                        )
                    )
                    + "</w:body></w:document>"
            )
        );

    /// <summary>A minimal presentation with one slide per string.</summary>
    public static byte[] Pptx(params string[] slides) =>
        Zip([
            (
                "[Content_Types].xml",
                "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"
            ),
            .. slides.Select(
                (text, i) =>
                    (
                        $"ppt/slides/slide{i + 1}.xml",
                        "<?xml version=\"1.0\"?><p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><p:cSld><p:spTree><p:sp><p:txBody><a:p><a:r><a:t>"
                            + System.Security.SecurityElement.Escape(text)
                            + "</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>"
                    )
            ),
        ]);

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Two seconds of ffmpeg's test pattern and a tone, encoded with <paramref name="encode"/> into a file named like
    /// <paramref name="name"/>, whose extension picks the container. Needs ffmpeg on PATH, as the worker does.
    /// </summary>
    public static byte[] Video(string name, params string[] encode)
    {
        var dir = Directory.CreateTempSubdirectory("dokument-video-").FullName;
        try
        {
            var path = Path.Combine(dir, name);
            var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
            foreach (
                var argument in (string[])
                    [
                        "-v",
                        "error",
                        "-f",
                        "lavfi",
                        "-i",
                        "testsrc=size=320x240:rate=25:duration=2",
                        "-f",
                        "lavfi",
                        "-i",
                        "sine=frequency=440:duration=2",
                        .. encode,
                        "-y",
                        path,
                    ]
            )
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0
                ? File.ReadAllBytes(path)
                : throw new InvalidOperationException($"ffmpeg couldn't make {name}: {error}");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
