namespace Dokument.Domain.Tags;

/// <summary>Where a tag on a file came from (docs/design.md §13.4). Clients show automatic tags differently.</summary>
public enum TagSource
{
    /// <summary>Someone added it to the file.</summary>
    User,

    /// <summary>An admin's keyword rule matched the file's text or details.</summary>
    Rule,

    /// <summary>The ML service suggested it (M7).</summary>
    Ml,
}
