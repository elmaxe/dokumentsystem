namespace Dokument.Domain.Search;

/// <summary>
/// What people changed about a file version's details (docs/design.md §13.5), over what the metadata stage read from its
/// bytes. What was read belongs to the bytes and is shared by every file with them, so edits belong to the version
/// instead: fixing your copy never changes someone else's. A new version starts with the previous one's edits. Null
/// means "as read from the file"; an empty title or description hides what was read. The original is never changed.
/// </summary>
public sealed class VersionDetails
{
    /// <summary>The longest title kept.</summary>
    public const int MaxTitleLength = 500;

    /// <summary>The longest description kept.</summary>
    public const int MaxDescriptionLength = 4000;

    public required Guid VersionId { get; init; }
    public string? Title { get; set; }
    public string? Description { get; set; }

    /// <summary>When it was taken, as people know it; see <see cref="TakenAt"/> for queries.</summary>
    public PartialDate? Taken
    {
        get =>
            TakenAt is { } at && TakenPrecision is { } precision
                ? PartialDate.From(at, precision)
                : null;
        set
        {
            TakenAt = value?.Start;
            TakenPrecision = value?.Precision;
        }
    }

    /// <summary>The first moment <see cref="Taken"/> could be, for the <c>taken:</c> filter (§13.3).</summary>
    public DateTimeOffset? TakenAt { get; private set; }
    public DatePrecision? TakenPrecision { get; private set; }
    public required Guid EditedBy { get; set; }
    public DateTimeOffset EditedAt { get; set; }

    public bool IsEmpty => Title is null && Description is null && Taken is null;

    /// <summary>The words search finds the file by: the edited title and description (content page 0 of the version).</summary>
    public string SearchText =>
        string.Join('\n', new[] { Title, Description }.Where(t => !string.IsNullOrWhiteSpace(t)));
}
