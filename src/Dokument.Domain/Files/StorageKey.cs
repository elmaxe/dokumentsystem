namespace Dokument.Domain.Files;

/// <summary>
/// Where a blob lives, relative to the originals volume: <c>blobs/{owner id}/ab/cd/abcd…</c> (docs/design.md §7). Each
/// person's files sit under their own folder; fanning out by the first two bytes keeps directories small at a million
/// files.
/// </summary>
public sealed record StorageKey
{
    private StorageKey(string value) => Value = value;

    public string Value { get; }

    public static StorageKey For(Guid ownerId, ContentHash hash)
    {
        var hex = hash.Hex;
        return new StorageKey($"blobs/{ownerId:D}/{hex[..2]}/{hex[2..4]}/{hex}");
    }

    /// <summary>For values read back from the database.</summary>
    public static StorageKey From(string value) =>
        value.StartsWith("blobs/", StringComparison.Ordinal)
        && !value.Contains("..", StringComparison.Ordinal)
            ? new StorageKey(value)
            : throw new ArgumentException("Not a blob storage key.", nameof(value));

    public override string ToString() => Value;
}
