using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Dokument.Domain.Search;

namespace Dokument.Domain.Tags;

/// <summary>
/// The words of a keyword rule as an admin types them: <c>kvitto, receipt, "tack för ditt köp"</c>. Each entry is a
/// word, which also matches its Swedish and English forms, or a phrase, which must appear word for word. Unlike search
/// there's no typo tolerance: one typo would let "kvitto" match "kvitta" (§13.4).
/// </summary>
public sealed partial record TagRuleWords
{
    public const int MaxEntries = 50;
    public const int MaxLength = 2000;

    private TagRuleWords(string value, IReadOnlyList<IReadOnlyList<string>> entries)
    {
        Value = value;
        Entries = entries;
    }

    /// <summary>The entries joined with ", ", phrases in quotes.</summary>
    public string Value { get; }

    /// <summary>Each entry's words, lowercased; a single word, or several for a phrase.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Entries { get; }

    /// <summary>Every word, for marking matches in the preview.</summary>
    public IReadOnlyList<string> AllWords => Entries.SelectMany(e => e).Distinct().ToList();

    public static bool TryParse(
        string? value,
        [NotNullWhen(true)] out TagRuleWords? words,
        [NotNullWhen(false)] out string? error
    )
    {
        words = null;
        value ??= "";
        if (value.Length > MaxLength)
        {
            error = $"The words can be at most {MaxLength} characters long.";
            return false;
        }

        var shown = new List<string>();
        var entries = new List<IReadOnlyList<string>>();
        foreach (Match part in Entry().Matches(value))
        {
            var text = part.Groups["quoted"].Success
                ? part.Groups["quoted"].Value
                : part.Groups["bare"].Value;
            var split = SearchQuery.Words(text);
            if (split.Count == 0 || entries.Any(e => e.SequenceEqual(split)))
            {
                continue;
            }

            entries.Add(split);
            shown.Add(split.Count > 1 ? $"\"{text.Trim()}\"" : text.Trim());
        }

        error = entries.Count switch
        {
            0 => "Give at least one word.",
            > MaxEntries => $"A rule can have at most {MaxEntries} words or phrases.",
            _ => null,
        };
        if (error is not null)
        {
            return false;
        }

        words = new TagRuleWords(string.Join(", ", shown), entries);
        return true;
    }

    /// <summary>For trusted values, such as rules read back from the database.</summary>
    public static TagRuleWords From(string value) =>
        TryParse(value, out var words, out var error)
            ? words
            : throw new ArgumentException(error, nameof(value));

    public bool Equals(TagRuleWords? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Value;

    [GeneratedRegex(@"""(?<quoted>[^""]*)""?|(?<bare>[^,""]+)")]
    private static partial Regex Entry();
}
