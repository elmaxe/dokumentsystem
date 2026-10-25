using System.Globalization;
using System.Text.RegularExpressions;

namespace Dokument.Domain.Search;

/// <summary>A date range, start inclusive, end exclusive, in UTC.</summary>
public sealed record DateRange(DateTimeOffset? From, DateTimeOffset? To)
{
    public bool IsEmpty => From is null && To is null;
}

/// <summary>
/// What someone typed in the search bar (docs/design.md §13.3), taken apart: words, "exact phrases", -excluded words,
/// and operators such as <c>type:pdf</c>, <c>in:Kvitton</c>, <c>after:2025-01-01</c>, <c>camera:fujifilm</c>,
/// <c>taken:2024</c> and <c>is:starred</c>. The filter chips are the same operators. Words are split the way the index
/// splits text (on anything that isn't a letter or digit) and lowercased. Unknown operators are searched as words.
/// </summary>
public sealed partial record SearchQuery
{
    public const int MaxTerms = 20;
    public const int MaxTermLength = 64;
    public const int MaxLength = 500;

    public IReadOnlyList<string> Terms { get; init; } = [];
    public IReadOnlyList<IReadOnlyList<string>> Phrases { get; init; } = [];
    public IReadOnlyList<string> Excluded { get; init; } = [];
    public FileType? Type { get; init; }
    public DateRange Modified { get; init; } = new(null, null);

    /// <summary>Folders with this name (any of them) and everything below, as in <c>in:"Kvitton 2025"</c>.</summary>
    public string? InFolderNamed { get; init; }
    public string? Camera { get; init; }
    public DateRange Taken { get; init; } = new(null, null);
    public bool Starred { get; init; }

    /// <summary>Whether there is anything to match as text, rather than only filters.</summary>
    public bool HasText => Terms.Count > 0 || Phrases.Count > 0;

    public bool HasFilters =>
        Type is not null
        || !Modified.IsEmpty
        || InFolderNamed is not null
        || Camera is not null
        || !Taken.IsEmpty
        || Starred;

    /// <summary>The words to mark in snippets and the viewer: the terms and the phrases' words.</summary>
    public IReadOnlyList<string> HighlightTerms =>
        Terms.Concat(Phrases.SelectMany(p => p)).Distinct().ToList();

    public static SearchQuery Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new SearchQuery();
        }

        input = input.Length > MaxLength ? input[..MaxLength] : input;
        var terms = new List<string>();
        var phrases = new List<IReadOnlyList<string>>();
        var excluded = new List<string>();
        var query = new SearchQuery();
        foreach (Match part in Part().Matches(input))
        {
            var negated = part.Groups["neg"].Success;
            var op = part.Groups["op"].Success ? part.Groups["op"].Value.ToLowerInvariant() : null;
            var quoted = part.Groups["quoted"].Success;
            var value = quoted ? part.Groups["quoted"].Value : part.Groups["bare"].Value;
            if (op is not null && !negated && WithOperator(query, op, value) is { } filtered)
            {
                query = filtered;
                continue;
            }

            var words = Words(op is null ? value : $"{op} {value}");
            if (negated)
            {
                excluded.AddRange(words);
            }
            else if (quoted && op is null && words.Count > 1)
            {
                phrases.Add(words);
            }
            else
            {
                terms.AddRange(words);
            }
        }

        return query with
        {
            Terms = terms.Distinct().Take(MaxTerms).ToList(),
            Phrases = phrases.Take(MaxTerms).ToList(),
            Excluded = excluded.Distinct().Take(MaxTerms).ToList(),
        };
    }

    private static SearchQuery? WithOperator(SearchQuery query, string op, string value)
    {
        value = value.Trim();
        switch (op)
        {
            case "type":
                return FileType.TryParse(value) is { } type ? query with { Type = type } : null;
            case "after":
                return Day(value) is { } after
                    ? query with
                    {
                        Modified = query.Modified with { From = after.From },
                    }
                    : null;
            case "before":
                return Day(value) is { } before
                    ? query with
                    {
                        Modified = query.Modified with { To = before.From },
                    }
                    : null;
            case "in":
                return value.Length > 0 ? query with { InFolderNamed = value } : null;
            case "camera":
                return value.Length > 0 ? query with { Camera = value } : null;
            case "taken":
                return Day(value) is { } taken ? query with { Taken = taken } : null;
            case "is" when value.Equals("starred", StringComparison.OrdinalIgnoreCase):
                return query with { Starred = true };
            default:
                return null;
        }
    }

    /// <summary>"2024" is that year, "2024-06" that month and "2024-06-14" that day.</summary>
    internal static DateRange? Day(string value)
    {
        string[] formats = ["yyyy-MM-dd", "yyyy-MM", "yyyy"];
        foreach (var format in formats)
        {
            if (
                DateTime.TryParseExact(
                    value,
                    format,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var start
                )
            )
            {
                var from = new DateTimeOffset(start, TimeSpan.Zero);
                var to = format switch
                {
                    "yyyy" => from.AddYears(1),
                    "yyyy-MM" => from.AddMonths(1),
                    _ => from.AddDays(1),
                };
                return new DateRange(from, to);
            }
        }

        return null;
    }

    /// <summary>Splits like the index does: runs of letters and digits, lowercased.</summary>
    public static List<string> Words(string text) =>
        Word()
            .Matches(text)
            .Select(m => m.Value.ToLowerInvariant())
            .Where(w => w.Length <= MaxTermLength)
            .ToList();

    [GeneratedRegex(
        @"(?<neg>-)?(?:(?<op>[A-Za-z]+):)?(?:""(?<quoted>[^""]*)""?|(?<bare>[^\s""]+))"
    )]
    private static partial Regex Part();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();
}
