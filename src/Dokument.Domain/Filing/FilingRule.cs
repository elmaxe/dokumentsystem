using Dokument.Domain.Tags;

namespace Dokument.Domain.Filing;

/// <summary>
/// A person's own filing rule (docs/design.md §13.6): when one of their files gets <see cref="Tag"/>, move it to
/// <see cref="Folder"/>. Rules are ordered, and the first that matches a file wins. One rule per tag.
/// </summary>
public sealed class FilingRule
{
    public const int MaxPerPerson = 100;

    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required Guid OwnerId { get; init; }

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
    public required FolderTemplate Folder { get; set; }

    /// <summary>The rule's place in the person's list, from 0. The first that matches wins.</summary>
    public int Position { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Since when the rule files files that get its tag: when it was made, turned on, or given another tag. Files that
    /// had the tag before then stay where they are, unless the person asks to file them too.
    /// </summary>
    public DateTimeOffset ActiveSince { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A file that a filing rule moved (docs/design.md §13.6). A file is filed at most once, so if its owner moves it back or
/// elsewhere, or undoes the move, it stays there.
/// </summary>
public sealed class NodeFiling
{
    public required Guid NodeId { get; init; }

    /// <summary>The rule that moved it, until the rule is deleted.</summary>
    public Guid? RuleId { get; init; }

    /// <summary>The tag that matched, kept for the message even after the rule is gone.</summary>
    public required string Tag { get; init; }

    /// <summary>Where it was, null for the top of My files.</summary>
    public Guid? FromFolderId { get; init; }
    public Guid? ToFolderId { get; init; }
    public DateTimeOffset FiledAt { get; init; }
    public DateTimeOffset? UndoneAt { get; set; }
}
