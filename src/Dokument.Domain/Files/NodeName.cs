using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Dokument.Domain.Files;

/// <summary>
/// The name of a file or folder (docs/design.md §6). Stored NFC-normalized; two names clash in a folder when their
/// <see cref="Key"/>s match, which ignores case. Never blank, never "." or "..", no slashes or control characters,
/// and at most 255 UTF-8 bytes so an export can write it to any common filesystem.
/// </summary>
public sealed record NodeName
{
    public const int MaxBytes = 255;

    private NodeName(string value)
    {
        Value = value;
        Key = value.ToLowerInvariant();
    }

    public string Value { get; }

    /// <summary>The case-insensitive form that must be unique within a folder.</summary>
    public string Key { get; }

    /// <summary>The name without its extension, e.g. "report" for "report.pdf". Dotfiles keep their name.</summary>
    public string Stem => Value.LastIndexOf('.') is > 0 and var dot ? Value[..dot] : Value;

    /// <summary>The extension with its dot, e.g. ".pdf", or empty.</summary>
    public string Extension => Value.LastIndexOf('.') is > 0 and var dot ? Value[dot..] : "";

    public static bool TryCreate(
        string? value,
        [NotNullWhen(true)] out NodeName? name,
        [NotNullWhen(false)] out string? error
    )
    {
        name = null;
        var normalized = value?.Trim().Normalize(NormalizationForm.FormC) ?? "";
        error = normalized switch
        {
            "" => "The name can't be empty.",
            "." or ".." => "The name can't be \".\" or \"..\".",
            _ when normalized.Any(c => c is '/' or '\\' || char.IsControl(c)) =>
                "The name can't contain slashes or control characters.",
            _ when Encoding.UTF8.GetByteCount(normalized) > MaxBytes =>
                $"The name can be at most {MaxBytes} bytes long.",
            _ => null,
        };
        if (error is not null)
        {
            return false;
        }

        name = new NodeName(normalized);
        return true;
    }

    /// <summary>For trusted values, such as names read back from the database.</summary>
    public static NodeName From(string value) =>
        TryCreate(value, out var name, out var error)
            ? name
            : throw new ArgumentException(error, nameof(value));

    /// <summary>"report (2).pdf" for n = 2: how a clashing upload is renamed when the user keeps both (§6).</summary>
    public NodeName WithCopyNumber(int n)
    {
        var suffix = $" ({n})";
        var stem = Stem;
        while (Encoding.UTF8.GetByteCount(stem + suffix + Extension) > MaxBytes && stem.Length > 1)
        {
            stem = stem[..^1];
        }

        return From(stem + suffix + Extension);
    }

    public bool Equals(NodeName? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Value;
}
