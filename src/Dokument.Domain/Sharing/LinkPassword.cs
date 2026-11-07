using System.Diagnostics.CodeAnalysis;

namespace Dokument.Domain.Sharing;

/// <summary>
/// A password the owner sets on a public link (§9): 4 to 128 characters, kept as typed. <see cref="ToString"/> never
/// prints it.
/// </summary>
public sealed record LinkPassword
{
    public const int MinLength = 4;
    public const int MaxLength = 128;

    private LinkPassword(string value) => Value = value;

    public string Value { get; }

    public static bool TryCreate(
        string? value,
        [NotNullWhen(true)] out LinkPassword? password,
        [NotNullWhen(false)] out string? error
    )
    {
        password = null;
        error = value?.Length switch
        {
            null or 0 => "Type a password.",
            < MinLength => $"The password needs at least {MinLength} characters.",
            > MaxLength => $"The password can be at most {MaxLength} characters.",
            _ => null,
        };
        if (error is not null)
        {
            return false;
        }

        password = new LinkPassword(value!);
        return true;
    }

    public override string ToString() => "LinkPassword(redacted)";
}
