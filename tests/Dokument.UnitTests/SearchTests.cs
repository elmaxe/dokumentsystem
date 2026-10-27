using System.Globalization;
using System.Text;
using Dokument.Domain.Search;
using Dokument.Tests;
using Dokument.Worker.Search;

namespace Dokument.UnitTests;

public class SearchQueryTests
{
    [Fact]
    public void Splits_words_phrases_exclusions_and_operators()
    {
        var query = SearchQuery.Parse(
            "IKEA-kvitto \"soffa 3-sits\" -faktura type:pdf in:\"Kvitton 2025\" after:2025-01 camera:fujifilm is:starred"
        );

        Assert.Equal(["ikea", "kvitto"], query.Terms);
        Assert.Equal(["soffa", "3", "sits"], Assert.Single(query.Phrases));
        Assert.Equal(["faktura"], query.Excluded);
        Assert.Equal("pdf", query.Type!.Name);
        Assert.Equal("Kvitton 2025", query.InFolderNamed);
        Assert.Equal(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), query.Modified.From);
        Assert.Null(query.Modified.To);
        Assert.Equal("fujifilm", query.Camera);
        Assert.True(query.Starred);
        Assert.Equal(["ikea", "kvitto", "soffa", "3", "sits"], query.HighlightTerms);
    }

    [Theory]
    [InlineData("taken:2024", "2024-01-01", "2025-01-01")]
    [InlineData("taken:2024-02", "2024-02-01", "2024-03-01")]
    [InlineData("taken:2024-02-29", "2024-02-29", "2024-03-01")]
    public void Dates_cover_the_whole_year_month_or_day(string input, string from, string to)
    {
        var taken = SearchQuery.Parse(input).Taken;
        Assert.Equal(DateTimeOffset.Parse(from + "T00:00:00Z"), taken.From);
        Assert.Equal(DateTimeOffset.Parse(to + "T00:00:00Z"), taken.To);
    }

    [Fact]
    public void Unknown_operators_and_bad_values_are_searched_as_words()
    {
        var query = SearchQuery.Parse("owner:bob type:nonsense taken:igår");
        Assert.Equal(["owner", "bob", "type", "nonsense", "taken", "igår"], query.Terms);
        Assert.False(query.HasFilters);
    }

    [Fact]
    public void Long_queries_are_capped()
    {
        var query = SearchQuery.Parse(
            string.Join(' ', Enumerable.Range(0, 100).Select(i => $"ord{i}"))
        );
        Assert.Equal(SearchQuery.MaxTerms, query.Terms.Count);
        Assert.Empty(SearchQuery.Parse(new string('a', SearchQuery.MaxTermLength + 1)).Terms);
    }
}

public class HighlighterTests
{
    [Theory]
    [InlineData("Kvittot", "kvitto", true)]
    [InlineData("kvitton", "kvitto", true)]
    [InlineData("receipts", "receipt", true)]
    [InlineData("kvittoarkivet", "kvitto", false)]
    [InlineData("hyresavtal", "hyresavtl", true)]
    [InlineData("ikea", "ika", false)]
    [InlineData("katt", "kat", false)]
    public void Marks_endings_and_typos_like_the_index_matches(
        string word,
        string term,
        bool expected
    ) => Assert.Equal(expected, Highlighter.Matches(word, term));

    [Fact]
    public void Snippets_center_on_the_hits()
    {
        var text =
            string.Join(' ', Enumerable.Repeat("fyllnad", 80))
            + "\n\nKvitto IKEA 2025-03-14"
            + new string(' ', 1)
            + string.Join(' ', Enumerable.Repeat("slut", 80));
        var snippet = Highlighter.Snip(text, ["ikea", "kvitto"]);

        Assert.True(snippet.CutStart);
        Assert.True(snippet.CutEnd);
        Assert.True(snippet.Text.Length <= Highlighter.SnippetLength + 40);
        Assert.Equal(["Kvitto", "IKEA"], snippet.Highlights.Select(h => snippet.Text[h[0]..h[1]]));
    }

    [Fact]
    public void Snippets_start_at_the_top_without_hits()
    {
        var snippet = Highlighter.Snip("Första raden\nandra raden", ["saknas"]);
        Assert.Equal("Första raden andra raden", snippet.Text);
        Assert.Empty(snippet.Highlights);
        Assert.False(snippet.CutStart);
    }
}

public class MetadataTests
{
    [Fact]
    public void Reads_camera_date_and_text_tags_from_exiftool_json()
    {
        var json = """
            [{"SourceFile":"x.jpg","Make":"FUJIFILM","Model":"X-T30","DateTimeOriginal":"2024:06:14 10:00:00",
              "OffsetTimeOriginal":"+02:00","Keywords":["Semester","Gotland"],"ImageWidth":6240,"Title":""}]
            """;
        var result = ExifTool.Parse(Encoding.UTF8.GetBytes(json));

        Assert.True(result.Readable);
        Assert.Equal("FUJIFILM X-T30", result.Camera);
        Assert.Equal(
            new DateTimeOffset(2024, 6, 14, 10, 0, 0, TimeSpan.FromHours(2)),
            result.TakenAt
        );
        Assert.Contains("FUJIFILM X-T30", result.SearchText);
        Assert.Contains("Semester, Gotland", result.SearchText);
        Assert.DoesNotContain("Title", result.Tags.Keys);
        Assert.True(result.TakenOffsetKnown);
        Assert.Null(result.Place);
    }

    [Fact]
    public void Reads_the_GPS_position_and_whether_the_camera_said_its_offset()
    {
        var json = """
            [{"SourceFile":"x.jpg","DateTimeOriginal":"2024:06:14 10:00:00","GPSPosition":"59.3157 -18.0335"}]
            """;
        var result = ExifTool.Parse(Encoding.UTF8.GetBytes(json));

        Assert.False(result.TakenOffsetKnown);
        Assert.Equal(new GeoPoint(59.3157, -18.0335), result.Place);
    }

    [Theory]
    [InlineData("0 0")]
    [InlineData("91 10")]
    [InlineData("59.3")]
    [InlineData("")]
    public void Ignores_GPS_without_a_fix(string value) => Assert.Null(ExifTool.PlaceOf(value));

    [Theory]
    [InlineData("Canon", "Canon EOS R5", "Canon EOS R5")]
    [InlineData("NIKON CORPORATION", "NIKON Z 6", "NIKON Z 6")]
    [InlineData("Apple", "iPhone 15", "Apple iPhone 15")]
    [InlineData(null, "X100V", "X100V")]
    [InlineData("Sony", null, "Sony")]
    public void Names_cameras_without_repeating_the_brand(
        string? make,
        string? model,
        string expected
    ) => Assert.Equal(expected, ExifTool.CameraOf(make, model));

    [Theory]
    [InlineData("2024:03:14 15:42:01", null, "2024-03-14T15:42:01+00:00")]
    [InlineData("2024:03:14 15:42:01.123+01:00", null, "2024-03-14T15:42:01+01:00")]
    [InlineData("2024:03:14 15:42:01", "-05:00", "2024-03-14T15:42:01-05:00")]
    public void Keeps_the_cameras_clock_and_offset(string value, string? offset, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected), ExifTool.DateOf(value, offset));

    [Theory]
    [InlineData("0000:00:00 00:00:00")]
    [InlineData("igår")]
    [InlineData("")]
    public void Ignores_empty_or_broken_dates(string value) =>
        Assert.Null(ExifTool.DateOf(value, null));
}

public class PartialDateTests
{
    [Theory]
    [InlineData("1974", DatePrecision.Year, "1974-01-01T00:00:00Z", "1974")]
    [InlineData(" 1974-07 ", DatePrecision.Month, "1974-07-01T00:00:00Z", "1974-07")]
    [InlineData("1974-07-14", DatePrecision.Day, "1974-07-14T00:00:00Z", "1974-07-14")]
    [InlineData("1974-07-14 15:30", DatePrecision.Time, "1974-07-14T15:30:00Z", "1974-07-14 15:30")]
    [InlineData(
        "1974-07-14T15:30:59",
        DatePrecision.Time,
        "1974-07-14T15:30:00Z",
        "1974-07-14 15:30"
    )]
    public void Reads_dates_known_to_the_year_month_day_or_minute(
        string input,
        DatePrecision precision,
        string start,
        string written
    )
    {
        Assert.True(PartialDate.TryParse(input, out var date));
        Assert.Equal(precision, date.Precision);
        Assert.Equal(DateTimeOffset.Parse(start, CultureInfo.InvariantCulture), date.Start);
        Assert.Equal(written, date.ToString());
    }

    [Theory]
    [InlineData("juli 1974")]
    [InlineData("1974-13")]
    [InlineData("74")]
    [InlineData("")]
    public void Refuses_anything_else(string input) =>
        Assert.False(PartialDate.TryParse(input, out _));
}

public class TextRecognitionTests
{
    private const string Header =
        "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n";

    [Fact]
    public void Rebuilds_lines_and_drops_unsure_words()
    {
        var tsv =
            Header
            + "5\t1\t1\t1\t1\t1\t10\t10\t100\t20\t96\tKvitto\n"
            + "5\t1\t1\t1\t1\t2\t120\t10\t80\t20\t91\tIKEA\n"
            + "5\t1\t1\t1\t2\t1\t10\t40\t60\t20\t30\t~~\n"
            + "5\t1\t1\t1\t2\t2\t10\t40\t60\t20\t88\tTotalt\n"
            + "5\t1\t2\t1\t1\t1\t10\t80\t60\t20\t90\t249\n";
        var result = TesseractOcr.ParseTsv(tsv, 200, 100);

        Assert.Equal("Kvitto IKEA\nTotalt\n\n249", result.Text);
        Assert.Equal(4, result.Words.Count);
        Assert.Equal(new OcrWord("IKEA", 0.6, 0.1, 0.4, 0.2), result.Words[1]);
    }

    [Fact]
    public void One_sure_word_is_text()
    {
        var tsv = Header + "5\t1\t1\t1\t1\t1\t10\t10\t100\t20\t95\tCollections\n";
        Assert.Equal("Collections", TesseractOcr.ParseTsv(tsv, 200, 100).Text);
    }

    [Fact]
    public void Moves_boxes_off_the_margin()
    {
        var padded = new OcrResult("Filmer", [new OcrWord("Filmer", 0.25, 0.25, 0.5, 0.5)]);
        var word = Assert.Single(TextExtractor.Unpad(padded, 100, 100, 50).Words);
        Assert.Equal(new OcrWord("Filmer", 0, 0, 1, 1), word);
    }

    [Fact]
    public async Task Reads_light_text_on_dark_up_to_the_edge()
    {
        // White on black with no margin, like a poster or a title card, which Tesseract misses as it is.
        using var text = NetVips.Image.Text("Filmer", font: "DejaVu Sans Bold 72", dpi: 150);
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(
                path,
                text.PngsaveBuffer(),
                TestContext.Current.CancellationToken
            );
            var result = await new TextExtractor(
                new TesseractOcr(
                    Microsoft.Extensions.Options.Options.Create(
                        new Dokument.Worker.Jobs.WorkerOptions()
                    )
                )
            ).ExtractAsync(path, null, ocr: true, TestContext.Current.CancellationToken);

            Assert.Equal("Filmer", Assert.Single(result.Pages).Text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Too_few_words_is_no_text()
    {
        var tsv = Header + "5\t1\t1\t1\t1\t1\t10\t10\t100\t20\t70\tbrus\n";
        Assert.Same(OcrResult.Empty, TesseractOcr.ParseTsv(tsv, 200, 100));
    }
}

public class OfficeTextTests
{
    [Fact]
    public void Reads_Word_paragraphs_and_one_page_per_slide()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var docx = Path.Combine(dir.FullName, "a.docx");
            File.WriteAllBytes(docx, TestFiles.Docx("Hyresavtal", "Hyran är 9 500 kr"));
            Assert.Equal(
                ["Hyresavtal\nHyran är 9 500 kr"],
                OfficeText.Read(docx, 10_000, TestContext.Current.CancellationToken)
            );

            var pptx = Path.Combine(dir.FullName, "b.pptx");
            File.WriteAllBytes(pptx, TestFiles.Pptx("Agenda", "Budget", "Frågor"));
            Assert.Equal(
                ["Agenda", "Budget", "Frågor"],
                OfficeText.Read(pptx, 10_000, TestContext.Current.CancellationToken)
            );
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}

public class MissingTesseractTests
{
    private sealed class MissingRecognizer : ITextRecognizer
    {
        public Task<string> EngineAsync(CancellationToken ct) =>
            throw new ToolMissingException("tesseract", new Exception());

        public Task<OcrResult> RecognizeAsync(
            byte[] png,
            int width,
            int height,
            CancellationToken ct
        ) => throw new ToolMissingException("tesseract", new Exception());
    }

    private static async Task<ExtractResult> ExtractAsync(byte[] bytes)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
            return await new TextExtractor(new MissingRecognizer()).ExtractAsync(
                path,
                null,
                ocr: true,
                TestContext.Current.CancellationToken
            );
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Keeps_a_PDFs_text_layer_and_skips_its_scans()
    {
        var result = await ExtractAsync(
            TestFiles.TextPdf("Hyresavtal för lägenheten på Storgatan", "", "Bilaga med villkoren")
        );

        Assert.Equal(ContentStatus.Ready, result.Status);
        Assert.Equal([1, 3], result.Pages.Select(p => p.PageNo));
        Assert.True(result.OcrSkipped);
        Assert.True(result.OcrUnavailable);
        Assert.Null(result.OcrEngine);
    }

    [Fact]
    public async Task Skips_photos()
    {
        var result = await ExtractAsync(TestFiles.TextImage("Kvitto från IKEA"));

        Assert.Equal(ContentStatus.Empty, result.Status);
        Assert.True(result.OcrSkipped);
        Assert.True(result.OcrUnavailable);
    }
}
