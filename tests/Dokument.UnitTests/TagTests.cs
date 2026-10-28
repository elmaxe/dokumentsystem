using Dokument.Domain.Search;
using Dokument.Domain.Tags;

namespace Dokument.UnitTests;

public class TagTests
{
    [Theory]
    [InlineData("  tax   2026 ", "tax 2026", "tax 2026")]
    [InlineData("Kvitto", "Kvitto", "kvitto")]
    [InlineData("étude", "étude", "étude")]
    public void Tags_are_trimmed_normalized_and_compared_without_case(
        string input,
        string value,
        string key
    )
    {
        var tag = TagName.From(input);
        Assert.Equal((value, key), (tag.Value, tag.Key));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a, b")]
    [InlineData("line\nbreak")]
    [InlineData("an extremely long tag that goes on and on past the limit")]
    public void Bad_tags_are_refused(string input) =>
        Assert.False(TagName.TryCreate(input, out _, out _));

    [Fact]
    public void Rule_words_split_on_commas_with_phrases_in_quotes()
    {
        var words = TagRuleWords.From(
            "Kvitto,  receipt , \"Tack för ditt köp\", OCR-nummer, kvitto,,"
        );
        Assert.Equal("Kvitto, receipt, \"Tack för ditt köp\", \"OCR-nummer\"", words.Value);
        Assert.Equal(
            [
                ["kvitto"],
                ["receipt"],
                ["tack", "för", "ditt", "köp"],
                ["ocr", "nummer"],
            ],
            words.Entries
        );
        Assert.False(TagRuleWords.TryParse(" , ", out _, out _));
    }

    [Fact]
    public void Tag_operator_filters_by_key()
    {
        var query = SearchQuery.Parse("tag:Receipt tag:\"Tax 2026\" ica");
        Assert.Equal(["receipt", "tax 2026"], query.Tags);
        Assert.Equal(["ica"], query.Terms);
        Assert.True(query.HasFilters);
    }
}
