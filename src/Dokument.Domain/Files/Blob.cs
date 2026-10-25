namespace Dokument.Domain.Files;

/// <summary>
/// One person's copy of some bytes on disk, stored once however many of their versions point at it (docs/design.md §6,
/// §7). Each person has their own copy, so the same bytes from two people are two blobs. <see cref="RefCount"/> is the
/// number of the owner's versions that use it; the purge job deletes the file once it reaches zero.
/// </summary>
public sealed class Blob
{
    public required Guid OwnerId { get; init; }
    public required ContentHash Hash { get; init; }
    public required long Size { get; init; }
    public required StorageKey StorageKey { get; init; }
    public int RefCount { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}
