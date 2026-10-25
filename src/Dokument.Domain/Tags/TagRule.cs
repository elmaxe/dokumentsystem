namespace Dokument.Domain.Tags;

/// <summary>
/// An admin's keyword rule (docs/design.md §13.4): files whose text or details contain any of the words get the tag.
/// One rule per tag. Rules run after a file's text is read, and over every file when one changes.
/// </summary>
public sealed class TagRule
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required TagName Tag
    {
        get;
        set
        {
            field = value;
            TagKey = value.Key;
        }
    }

    public string TagKey { get; private set; } = "";
    public required TagRuleWords Words { get; set; }
    public bool Enabled { get; set; } = true;
    public Guid? CreatedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}
