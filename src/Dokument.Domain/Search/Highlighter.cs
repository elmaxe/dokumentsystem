using System.Text.RegularExpressions;

namespace Dokument.Domain.Search;

/// <summary>A piece of text with the ranges to mark, as <c>[start, end)</c> offsets into <see cref="Text"/>.</summary>
public sealed record Snippet(
    string Text,
    IReadOnlyList<int[]> Highlights,
    bool CutStart,
    bool CutEnd
);

/// <summary>
/// Marks search hits in text (docs/design.md §13.3). The index matches Swedish and English stems, so a word counts as a
/// hit when it is the term, or one is the other plus a short ending ("kvitto" and "kvittot", "receipt" and
/// "receipts"), or, for longer words, one typo away. The clients use the same rule to mark hits in the viewer. Snippets
/// are plain text with offsets, never HTML, so file content can't inject markup.
/// </summary>
public static partial class Highlighter
{
    /// <summary>How long a snippet is, about two lines in a result row.</summary>
    public const int SnippetLength = 220;

    /// <summary>The longest ending that still counts as the same word.</summary>
    private const int MaxEnding = 4;

    public static bool Matches(string word, string term)
    {
        word = word.ToLowerInvariant();
        if (word == term)
        {
            return true;
        }

        var (shorter, longer) = word.Length <= term.Length ? (word, term) : (term, word);
        if (
            shorter.Length >= 4
            && longer.Length - shorter.Length <= MaxEnding
            && longer.StartsWith(shorter, StringComparison.Ordinal)
        )
        {
            return true;
        }

        return shorter.Length >= 5
            && longer.Length - shorter.Length <= 1
            && OneEditApart(word, term);
    }

    /// <summary>
    /// The part of <paramref name="text"/> with the most hits, about <see cref="SnippetLength"/> characters, with line
    /// breaks folded into spaces. Starts at the beginning when nothing matches, as for a file found by its name.
    /// </summary>
    public static Snippet Snip(
        string text,
        IReadOnlyCollection<string> terms,
        int length = SnippetLength
    )
    {
        var flat = Spaces().Replace(text, " ").Trim();
        var hits = Hits(flat, terms);
        var start = 0;
        if (hits.Count > 0)
        {
            // The window that holds the most hits, then a little context before its first one.
            var best = 0;
            var bestCount = 0;
            for (int i = 0, j = 0; i < hits.Count; i++)
            {
                while (j < hits.Count && hits[j][1] - hits[i][0] <= length * 2 / 3)
                {
                    j++;
                }

                if (j - i > bestCount)
                {
                    (best, bestCount) = (i, j - i);
                }
            }

            start = Math.Max(0, hits[best][0] - (length / 4));
        }

        // Whole words at both ends.
        if (start > 0)
        {
            var space = flat.IndexOf(' ', start);
            start = space >= 0 && space < start + 20 ? space + 1 : start;
        }

        var end = Math.Min(flat.Length, start + length);
        if (end < flat.Length)
        {
            var space = flat.LastIndexOf(' ', end);
            end = space > start + (length / 2) ? space : end;
        }

        var highlights = hits.Where(h => h[0] >= start && h[1] <= end)
            .Select(h => new[] { h[0] - start, h[1] - start })
            .ToList();
        return new Snippet(flat[start..end], highlights, start > 0, end < flat.Length);
    }

    /// <summary>Every word of <paramref name="text"/> that matches one of <paramref name="terms"/>, as <c>[start, end)</c>.</summary>
    public static List<int[]> Hits(string text, IReadOnlyCollection<string> terms)
    {
        var hits = new List<int[]>();
        if (terms.Count == 0)
        {
            return hits;
        }

        foreach (Match word in Word().Matches(text))
        {
            if (terms.Any(t => Matches(word.Value, t)))
            {
                hits.Add([word.Index, word.Index + word.Length]);
            }
        }

        return hits;
    }

    /// <summary>One insertion, deletion, substitution or swap of neighbours apart.</summary>
    private static bool OneEditApart(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1)
        {
            return false;
        }

        var i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i])
        {
            i++;
        }

        if (a.Length == b.Length)
        {
            return a[(i + 1)..] == b[(i + 1)..]
                || (
                    i + 1 < a.Length
                    && a[i] == b[i + 1]
                    && a[i + 1] == b[i]
                    && a[(i + 2)..] == b[(i + 2)..]
                );
        }

        return a.Length > b.Length ? a[(i + 1)..] == b[i..] : a[i..] == b[(i + 1)..];
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
