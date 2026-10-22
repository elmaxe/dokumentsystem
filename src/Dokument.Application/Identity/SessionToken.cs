using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Dokument.Application.Identity;

/// <summary>Opaque session tokens: 256 random bits, handed to the client once and stored only as a SHA-256 hash.</summary>
public static class SessionToken
{
    public static string Create() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    /// <summary>
    /// The CSRF token for a cookie session. It is derived from the session token, so it needs no storage, but it
    /// can't be turned back into the session token. Scripts on other origins can't read it, so a forged request lacks it.
    /// </summary>
    public static string CsrfToken(string token) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes("csrf:" + token)));

    public static bool CsrfTokenMatches(string token, string? candidate) =>
        candidate is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(CsrfToken(token)), Encoding.UTF8.GetBytes(candidate));
}
