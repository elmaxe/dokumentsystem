using System.Security.Cryptography;
using System.Text;
using Dokument.Domain.Sharing;
using Konscious.Security.Cryptography;

namespace Dokument.Infrastructure.Sharing;

/// <summary>
/// Hashes public-link passwords (docs/design.md §21). New hashes use <see cref="Current"/>; a hash is checked with the
/// settings of the version it was made with, so raising them later doesn't break existing links.
/// </summary>
public static class LinkPasswordHasher
{
    public const PasswordHashVersion Current = PasswordHashVersion.Argon2idV1;

    public static PasswordHash Hash(LinkPassword password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return PasswordHash.Create(Current, salt, Compute(Current, password.Value, salt));
    }

    public static bool Verify(string password, PasswordHash hash) =>
        CryptographicOperations.FixedTimeEquals(
            Compute(hash.Version, password, hash.Salt.ToArray()),
            hash.Hash
        );

    private static byte[] Compute(PasswordHashVersion version, string password, byte[] salt) =>
        version switch
        {
            PasswordHashVersion.Argon2idV1 => Argon2id(
                password,
                salt,
                memoryKib: 19 * 1024,
                iterations: 2,
                lanes: 1,
                length: 32
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(version), version, null),
        };

    private static byte[] Argon2id(
        string password,
        byte[] salt,
        int memoryKib,
        int iterations,
        int lanes,
        int length
    )
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKib,
            Iterations = iterations,
            DegreeOfParallelism = lanes,
        };
        return argon.GetBytes(length);
    }
}
