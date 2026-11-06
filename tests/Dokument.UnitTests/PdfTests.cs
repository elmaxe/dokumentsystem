using Dokument.Domain.Files;
using Dokument.Domain.Previews;

namespace Dokument.UnitTests;

public sealed class PdfTests
{
    [Theory]
    [InlineData("1-3, 5", 10, "1-3 5-5")]
    [InlineData("4–6,2", 10, "4-6 2-2")]
    [InlineData(" 7 - 7 ", 7, "7-7")]
    public void Page_ranges_are_read_as_people_type_them(string text, int pages, string expected)
    {
        Assert.True(PageRanges.TryParse(text, pages, out var ranges, out var error), error);
        Assert.Equal(expected, string.Join(' ', ranges.Select(r => $"{r.First}-{r.Last}")));
    }

    [Theory]
    [InlineData("", "Give at least one page.")]
    [InlineData("0", "\"0\" is outside pages 1 to 5.")]
    [InlineData("4-9", "\"4-9\" is outside pages 1 to 5.")]
    [InlineData("3-1", "\"3-1\" goes backwards.")]
    [InlineData("a", "\"a\" isn't a page or a range of pages.")]
    [InlineData("1-2-3", "\"1-2-3\" isn't a page or a range of pages.")]
    public void Bad_page_ranges_say_what_is_wrong(string text, string expected)
    {
        Assert.False(PageRanges.TryParse(text, 5, out _, out var error));
        Assert.Equal(expected, error);
    }

    [Fact]
    public void Page_sheets_hold_a_hundred_pages_each()
    {
        var hash = ContentHash.FromBytes(new byte[32]);
        Assert.Equal(1, PageSheetLayout.SheetCount(100));
        Assert.Equal(2, PageSheetLayout.SheetCount(101));
        Assert.Equal(
            ["pages-1.webp", "pages-2.webp"],
            PageSheetLayout.Keys(hash, 150).Select(k => Path.GetFileName(k.Value))
        );
        Assert.Equal(
            DerivedKey.ForPageSheet(hash, 1),
            DerivedKey.For(hash, RenditionKind.PageSheets)
        );
    }
}
