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
        return red.Bandjoin(green, blue).Cast(Enums.BandFormat.Uchar).Copy(interpretation: Enums.Interpretation.Srgb);
    }

    public static byte[] Png(int width, int height)
    {
        using var image = Gradient(width, height);
        return image.PngsaveBuffer();
    }

    /// <summary>A JPEG whose pixels are stored sideways, with EXIF orientation 6 ("rotate 90° clockwise to view").</summary>
    public static byte[] SidewaysJpeg(int storedWidth, int storedHeight)
    {
        using var image = Gradient(storedWidth, storedHeight).Mutate(m => m.Set(GValue.GIntType, "orientation", 6));
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
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents {content} 0 R >>");
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

        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
