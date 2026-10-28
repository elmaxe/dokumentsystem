using Dokument.Domain.Filing;

namespace Dokument.UnitTests;

public sealed class FolderTemplateTests
{
    [Theory]
    [InlineData(" Kvitton / {year}/ ", "Kvitton/{year}")]
    [InlineData("Räkningar/{YEAR}-{month}", "Räkningar/{YEAR}-{month}")]
    [InlineData("Avtal", "Avtal")]
    public void Folders_are_trimmed_and_empty_ones_dropped(string value, string expected)
    {
        Assert.True(FolderTemplate.TryCreate(value, out var template, out _));
        Assert.Equal(expected, template.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" / ")]
    [InlineData("Kvitton/{day}")]
    [InlineData("Kvitton/{year")]
    [InlineData("a/b/c/d/e/f/g/h/i/j/k")]
    public void Bad_templates_are_refused(string value) =>
        Assert.False(FolderTemplate.TryCreate(value, out _, out _));

    [Fact]
    public void The_month_has_two_digits_so_folders_sort()
    {
        var template = FolderTemplate.From("Räkningar/{year}/{month}");
        var folders = template.Resolve(
            new DateTimeOffset(2026, 3, 14, 15, 42, 0, TimeSpan.FromHours(1))
        );
        Assert.Equal(["Räkningar", "2026", "03"], folders.Select(f => f.Value));
    }
}
