namespace Dokument.Domain.Files;

/// <summary>
/// The last thing someone did with a file themselves, for their Recent page (docs/design.md §15.2). One row per person
/// and file, overwritten each time. Only that person sees it, and it is not the audit log: opening a preview is
/// recorded here but never audited (§18).
/// </summary>
public sealed class RecentFile
{
    public required Guid UserId { get; init; }
    public required Guid NodeId { get; init; }
    public DateTimeOffset At { get; set; }
    public RecentAction Action { get; set; }
}

public enum RecentAction
{
    Opened,
    Downloaded,
    Uploaded,
    Restored,
}
