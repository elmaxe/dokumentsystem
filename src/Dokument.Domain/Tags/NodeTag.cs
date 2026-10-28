namespace Dokument.Domain.Tags;

/// <summary>A tag someone added to a file (docs/design.md §13.4). It belongs to the file, so it stays through new versions.</summary>
public sealed class NodeTag
{
    public required Guid NodeId { get; init; }

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
    public required Guid AddedBy { get; init; }
    public DateTimeOffset AddedAt { get; init; }
}

/// <summary>
/// An automatic tag someone took off a file (docs/design.md §13.4). Rules running again, or a new version that would
/// get the tag, don't bring it back on this file. Adding the tag again removes the record.
/// </summary>
public sealed class NodeTagRemoval
{
    public required Guid NodeId { get; init; }

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
    public required Guid RemovedBy { get; init; }
    public DateTimeOffset RemovedAt { get; init; }
}
