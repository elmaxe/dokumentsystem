using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Dokument.Domain.Sharing;

/// <summary>
/// The secret part of a public link, <c>/s/{token}</c>: 128 random bits as base64url, 22 characters.
/// <see cref="ToString"/> never prints it, so it can't leak into logs.
/// </summary>
public sealed record LinkToken
{
    private const int ByteLength = 16;
    private static readonly int EncodedLength = Base64Url.GetEncodedLength(ByteLength);

    private LinkToken(string value) => Value = value;

    public string Value { get; }

    public static LinkToken Create() =>
        new(Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(ByteLength)));

    /// <summary>Accepts only well-formed tokens, so a guess that can't be one never reaches the database.</summary>
    public static bool TryParse(string? value, out LinkToken token)
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

        token = new LinkToken(value);
        return true;
    }

    /// <summary>A token read back from storage, which is always well formed.</summary>
    public static LinkToken Parse(string value) =>
        TryParse(value, out var token) ? token : throw new FormatException("Not a link token.");

    public bool Equals(LinkToken? other) =>
        other is not null
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Value),
            Encoding.UTF8.GetBytes(other.Value)
        );

    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => "LinkToken(redacted)";
}
