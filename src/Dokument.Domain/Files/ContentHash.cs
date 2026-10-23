using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace Dokument.Domain.Files;

/// <summary>The SHA-256 of a file's bytes: the identity of a blob (docs/design.md §6, §7) and the duplicate key (§8.3).</summary>
public sealed record ContentHash
{
    public const int ByteLength = SHA256.HashSizeInBytes;

    private readonly byte[] bytes;

    private ContentHash(byte[] bytes) => this.bytes = bytes;

    public ReadOnlySpan<byte> Bytes => bytes;

    /// <summary>Lowercase hex, as clients send it.</summary>
    public string Hex => Convert.ToHexStringLower(bytes);

    public static ContentHash FromBytes(ReadOnlySpan<byte> value) =>
        value.Length == ByteLength
            ? new ContentHash(value.ToArray())
            : throw new ArgumentException("A SHA-256 is 32 bytes.", nameof(value));

    /// <summary>Accepts 64 hex digits in either case.</summary>
    public static bool TryParse(string? hex, [NotNullWhen(true)] out ContentHash? hash)
    {
        hash = null;
        if (hex is not { Length: ByteLength * 2 } || !hex.All(char.IsAsciiHexDigit))
        {
            return false;
        }

        hash = new ContentHash(Convert.FromHexString(hex));
        return true;
    }

    public static async Task<ContentHash> ComputeAsync(
        Stream stream,
        CancellationToken ct = default
    ) => new(await SHA256.HashDataAsync(stream, ct));

    public byte[] ToArray() => (byte[])bytes.Clone();

    public bool Equals(ContentHash? other) =>
        other is not null && bytes.AsSpan().SequenceEqual(other.bytes);

    public override int GetHashCode() => BitConverter.ToInt32(bytes, 0);

    public override string ToString() => Hex;
}
