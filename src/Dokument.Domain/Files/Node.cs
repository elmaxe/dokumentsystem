namespace Dokument.Domain.Files;

/// <summary>
/// A file or folder in someone's tree (docs/design.md §6). A file's bytes live in its versions; the node is the
/// logical file the user sees. A null <see cref="ParentId"/> means the top of the owner's "My files".
/// </summary>
public sealed class Node
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required Guid OwnerId { get; init; }
    public Guid? ParentId { get; private set; }
    public required NodeKind Kind { get; init; }
    public required NodeName Name
    {
        get;
        set
        {
            field = value;
            NameKey = value.Key;
        }
    }

    /// <summary>The case-insensitive name, unique among the live nodes in a folder.</summary>
    public string NameKey { get; private set; } = "";

    /// <summary>Ancestor ids then this node's id, as an ltree. Makes subtree queries (move, trash, folder size) one index scan.</summary>
    public string Path { get; private set; } = "";
    public Guid? CurrentVersionId { get; set; }
    public FileVersion? CurrentVersion { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ModifiedAt { get; set; }
    public DateTimeOffset? TrashedAt { get; set; }
    public Guid? TrashedRootId { get; set; }

    public bool IsFolder => Kind == NodeKind.Folder;
    public bool IsTrashed => TrashedAt is not null;

    /// <summary>The ltree label for a node id: ltree labels can't hold every uuid character on older Postgres, so hex only.</summary>
    public static string Label(Guid id) => id.ToString("N");

    /// <summary>Places the node under <paramref name="parent"/> (null for the root). Moving a folder also needs its descendants' paths rewritten.</summary>
    public void PlaceUnder(Node? parent)
    {
        if (parent is { IsFolder: false })
        {
            throw new InvalidOperationException("Only folders can hold other nodes.");
        }

        ParentId = parent?.Id;
        Path = parent is null ? Label(Id) : $"{parent.Path}.{Label(Id)}";
    }

    /// <summary>True if <paramref name="other"/> is this node or one of its descendants.</summary>
    public bool Contains(Node other) =>
        other.Path == Path || other.Path.StartsWith(Path + ".", StringComparison.Ordinal);
}

public enum NodeKind
{
    Folder,
    File,
}
