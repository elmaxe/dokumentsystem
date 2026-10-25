using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Dokument.Domain.Tags;

/// <summary>
/// A tag, such as "receipt" or "tax 2026" (docs/design.md §13.4). Trimmed, NFC, at most <see cref="MaxLength"/>
/// characters, with no commas (they separate tags when typing), line breaks or control characters. Two tags are the
/// same when their <see cref="Key"/>s match, which ignores case.
/// </summary>
public sealed record TagName
{
    public const int MaxLength = 50;

    private TagName(string value)
    {
        Value = value;
        Key = value.ToLowerInvariant();
    }

    public string Value { get; }

    /// <summary>The case-insensitive form: one tag per key on a file.</summary>
    public string Key { get; }

    public static bool TryCreate(
        string? value,
        [NotNullWhen(true)] out TagName? tag,
        [NotNullWhen(false)] out string? error
    )
    {
        tag = null;
        var normalized = string.Join(
            ' ',
            (value ?? "")
                .Normalize(NormalizationForm.FormC)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        );
        error = normalized switch
        {
            "" => "A tag can't be empty.",
            _ when normalized.Any(c => c == ',' || char.IsControl(c)) =>
                "A tag can't contain commas or line breaks.",
            _ when normalized.Length > MaxLength =>
                $"A tag can be at most {MaxLength} characters long.",
            _ => null,
        };
        if (error is not null)
        {
            return false;
        }

        tag = new TagName(normalized);
        return true;
    }

    /// <summary>For trusted values, such as tags read back from the database.</summary>
    public static TagName From(string value) =>
        TryCreate(value, out var tag, out var error)
            ? tag
            : throw new ArgumentException(error, nameof(value));

    public bool Equals(TagName? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Value;
}
