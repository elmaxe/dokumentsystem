namespace Dokument.Domain.Files;

/// <summary>
/// Someone starred a file or folder (docs/design.md §6, §15.2). Stars are each person's own, so a shared item can be
/// starred by one person and not another.
/// </summary>
public sealed class Star
{
    public required Guid UserId { get; init; }
    public required Guid NodeId { get; init; }
    public DateTimeOffset StarredAt { get; init; }
}
