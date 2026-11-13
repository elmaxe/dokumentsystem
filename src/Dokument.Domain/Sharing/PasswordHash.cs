using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace Dokument.Domain.Sharing;

/// <summary>
/// A stored password hash (docs/design.md §9, §21): one byte array whose first byte is the <see cref="Version"/>, then
/// that version's salt and hash. The version names the algorithm and its settings, so new hashes can use stronger
/// ones while older hashes still verify with theirs. <see cref="ToString"/> never prints it.
/// </summary>
public sealed record PasswordHash
{
    private readonly byte[] bytes;

    private PasswordHash(byte[] bytes) => this.bytes = bytes;

    /// <summary>How the hash was made; see <see cref="PasswordHashVersion"/>.</summary>
    public PasswordHashVersion Version => (PasswordHashVersion)bytes[0];

    public ReadOnlySpan<byte> Salt => bytes.AsSpan(1, Layout(Version).SaltBytes);

    public ReadOnlySpan<byte> Hash => bytes.AsSpan(1 + Layout(Version).SaltBytes);

    public static PasswordHash Create(PasswordHashVersion version, byte[] salt, byte[] hash)
    {
        var (saltBytes, hashBytes) = Layout(version);
        if (salt.Length != saltBytes || hash.Length != hashBytes)
        {
            throw new ArgumentException(
                $"Version {version} takes a {saltBytes}-byte salt and a {hashBytes}-byte hash."
            );
        }

        return new PasswordHash([(byte)version, .. salt, .. hash]);
    }

    /// <summary>Accepts only a known version with that version's length, so a damaged value never verifies.</summary>
    public static bool TryFromBytes(byte[]? value, [NotNullWhen(true)] out PasswordHash? hash)
    {
        hash = null;
        if (value is not [var first, ..] || !Enum.IsDefined((PasswordHashVersion)first))
        {
            return false;
        }

        var (saltBytes, hashBytes) = Layout((PasswordHashVersion)first);
        if (value.Length != 1 + saltBytes + hashBytes)
        {
            return false;
        }

        hash = new PasswordHash([.. value]);
        return true;
    }

    public static PasswordHash FromBytes(byte[] value) =>
        TryFromBytes(value, out var hash)
            ? hash
            : throw new ArgumentException("Not a password hash of a known version.", nameof(value));

    public byte[] ToBytes() => [.. bytes];

    /// <summary>
    /// Changes whenever the password is set, since each hash has its own salt: what a link's unlock cookie is bound
    /// to, so setting a new password lets nobody in on the old one (§9).
    /// </summary>
    public string Stamp() => Convert.ToHexString(SHA256.HashData(bytes))[..16];

    public bool Equals(PasswordHash? other) =>
        other is not null && CryptographicOperations.FixedTimeEquals(bytes, other.bytes);

    public override int GetHashCode() => bytes[0];

    public override string ToString() => $"PasswordHash(v{(byte)Version}, redacted)";

    private static (int SaltBytes, int HashBytes) Layout(PasswordHashVersion version) =>
        version switch
        {
            PasswordHashVersion.Argon2idV1 => (16, 32),
            _ => throw new ArgumentOutOfRangeException(nameof(version), version, null),
        };
}

/// <summary>The algorithm and settings of a <see cref="PasswordHash"/>. Add a version; never change one.</summary>
public enum PasswordHashVersion : byte
{
    /// <summary>Argon2id, 19 MiB, two passes, one lane (OWASP's minimum); 16-byte salt, 32-byte hash.</summary>
    Argon2idV1 = 1,
}
