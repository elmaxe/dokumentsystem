using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Dokument.Domain.Identity;

/// <summary>
/// An opaque session token: 256 random bits as base64url. It is handed to the client once and stored only as
/// <see cref="Hash"/>. <see cref="ToString"/> never prints it, so it can't leak into logs.
/// </summary>
public sealed record SessionToken
{
    private const int ByteLength = 32;
    private static readonly int EncodedLength = Base64Url.GetEncodedLength(ByteLength);

    private SessionToken(string value) => Value = value;

    /// <summary>The secret itself, for the cookie or the bearer response only.</summary>
    public string Value { get; }

    public static SessionToken Create() =>
        new(Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(ByteLength)));

    /// <summary>Accepts only well-formed tokens, so garbage from a client never reaches the database.</summary>
    public static bool TryParse(string? value, out SessionToken token)
    {
        token = null!;
        if (
            value is null
            || value.Length != EncodedLength
            || !Base64Url.IsValid(value, out var decodedLength)
            || decodedLength != ByteLength
        )
        {
            return false;
        }

        token = new SessionToken(value);
        return true;
    }

    public byte[] Hash() => SHA256.HashData(Encoding.UTF8.GetBytes(Value));

    /// <summary>
    /// The CSRF token for a cookie session. It is derived from the session token, so it needs no storage, but it
    /// can't be turned back into the session token. Scripts on other origins can't read it, so a forged request lacks it.
    /// </summary>
    public string CsrfToken() =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes("csrf:" + Value)));

    public bool CsrfTokenMatches(string? candidate) =>
        candidate is not null
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(CsrfToken()),
            Encoding.UTF8.GetBytes(candidate)
        );

    public bool Equals(SessionToken? other) =>
        other is not null
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Value),
            Encoding.UTF8.GetBytes(other.Value)
        );

    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => "SessionToken(redacted)";
}
