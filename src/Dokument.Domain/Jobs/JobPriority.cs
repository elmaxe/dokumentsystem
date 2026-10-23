namespace Dokument.Domain.Jobs;

/// <summary>Higher runs first (docs/design.md §10.1): what a user just did comes before backfills and rescans.</summary>
public enum JobPriority
{
    Background = 0,
    Interactive = 10,
}
