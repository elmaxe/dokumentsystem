namespace Dokument.Domain.Files;

/// <summary>
/// Bytes on disk, stored once however many versions point at them (docs/design.md §6, §7). <see cref="RefCount"/> is the
/// number of versions that use it; the purge job deletes the file once it reaches zero.
/// </summary>
public sealed class Blob
{
    public required ContentHash Hash { get; init; }
    public required long Size { get; init; }
    public required StorageKey StorageKey { get; init; }
    public int RefCount { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}
