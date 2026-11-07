using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dokument.Domain.Sharing;
using Konscious.Security.Cryptography;

namespace Dokument.Infrastructure.Sharing;

/// <summary>
/// Hashes public-link passwords with Argon2id (docs/design.md §21), using OWASP's minimum settings: 19 MiB, two passes,
/// one lane. The hash is kept in PHC form, so the settings can be raised later without breaking old links.
/// </summary>
public static class LinkPasswordHasher
{
    private const int MemoryKib = 19 * 1024;
    private const int Iterations = 2;
    private const int Parallelism = 1;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string Hash(LinkPassword password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Compute(password.Value, salt, MemoryKib, Iterations, Parallelism, HashBytes);
        return $"$argon2id$v=19$m={MemoryKib},t={Iterations},p={Parallelism}${B64(salt)}${B64(hash)}";
    }

    public static bool Verify(string password, string phc)
    {
        var parts = phc.Split('$');
        if (parts is not ["", "argon2id", "v=19", var settings, var salt, var expected])
        {
            return false;
        }

        var values = settings
            .Split(',')
            .Select(p => p.Split('='))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => int.Parse(p[1], CultureInfo.InvariantCulture));
        var expectedBytes = Convert.FromBase64String(Pad(expected));
        var actual = Compute(
            password,
            Convert.FromBase64String(Pad(salt)),
            values["m"],
            values["t"],
            values["p"],
            expectedBytes.Length
        );
        return CryptographicOperations.FixedTimeEquals(actual, expectedBytes);
    }

    /// <summary>Changes whenever the password does, so being let in with an old password ends (§9).</summary>
    public static string Stamp(string phc) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(phc)))[..16];

    private static byte[] Compute(
        string password,
        byte[] salt,
        int memoryKib,
        int iterations,
        int parallelism,
        int length
    )
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKib,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon.GetBytes(length);
    }

    // PHC strings use standard base64 without padding.
    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static string Pad(string value) =>
        value.PadRight(value.Length + (4 - value.Length % 4) % 4, '=');
}
