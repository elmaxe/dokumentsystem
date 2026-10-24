namespace Dokument.Domain.Identity;

/// <summary>A storage limit in bytes, or none. The admin's default quota for people without their own (§17).</summary>
public sealed record StorageLimit
{
    public static readonly StorageLimit None = new((long?)null);

    private StorageLimit(long? bytes) => Bytes = bytes;

    /// <summary>Null means unlimited.</summary>
    public long? Bytes { get; }

    public static StorageLimit Of(long bytes) =>
        bytes > 0
            ? new StorageLimit(bytes)
            : throw new ArgumentOutOfRangeException(nameof(bytes), "A limit must be positive.");

    public static StorageLimit From(long? bytes) => bytes is { } b ? Of(b) : None;
}
