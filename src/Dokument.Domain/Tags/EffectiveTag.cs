namespace Dokument.Domain.Tags;

/// <summary>
/// A tag as a file shows it (docs/design.md §13.4): the tags people added, plus the automatic tags of its current
/// bytes that nobody removed from it. Read from the <c>effective_node_tags</c> view. A tag both added by someone and
/// given by a rule shows once, as added by someone.
/// </summary>
public sealed class EffectiveTag
{
    public Guid NodeId { get; init; }
    public required string Tag { get; init; }
    public required string TagKey { get; init; }
    public TagSource Source { get; init; }
    public Guid? RuleId { get; init; }
}
