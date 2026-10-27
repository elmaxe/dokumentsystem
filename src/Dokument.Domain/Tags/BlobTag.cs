using Dokument.Domain.Files;

namespace Dokument.Domain.Tags;

/// <summary>
/// An automatic tag (docs/design.md §13.4). Like text and thumbnails it belongs to the bytes, so identical files get
/// it once and a copy has it at once. Files show it unless someone removed it there (<see cref="NodeTagRemoval"/>).
/// </summary>
public sealed class BlobTag
{
    public required ContentHash BlobHash { get; init; }

    public required TagName Tag
    {
        get;
        init
        {
            field = value;
            TagKey = value.Key;
        }
    }

    public string TagKey { get; private set; } = "";
    public required TagSource Source { get; init; }

    /// <summary>The rule that added it, for <see cref="TagSource.Rule"/>.</summary>
    public Guid? RuleId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
