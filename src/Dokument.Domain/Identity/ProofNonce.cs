using System.Buffers.Text;

namespace Dokument.Domain.Identity;

/// <summary>
/// A fresh challenge from the mobile app, which asks a local address to prove it's the server that issued the
/// app's session (docs/design.md §15.3): 16 to 64 random bytes as base64url.
/// </summary>
public sealed record ProofNonce
{
    private const int MinBytes = 16;
    private const int MaxBytes = 64;

    private ProofNonce(string value) => Value = value;

    public string Value { get; }

    public static bool TryParse(string? value, out ProofNonce nonce)
    {
        nonce = null!;
        if (
            value is null
            || value.Length > Base64Url.GetEncodedLength(MaxBytes)
            || !Base64Url.IsValid(value, out var decodedLength)
            || decodedLength is < MinBytes or > MaxBytes
        )
        {
            return false;
        }

        nonce = new ProofNonce(value);
        return true;
    }

    public override string ToString() => Value;
}
