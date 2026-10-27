using System.Text.Json.Nodes;
using Dokument.Domain.Search;
using Dokument.Domain.Tags;

namespace Dokument.Infrastructure.Search;

/// <summary>
/// Builds pg_search queries (docs/design.md §13.1) as its JSON query language, so what someone types is always data
/// and never query syntax. Field names are those of the BM25 indexes the migration creates.
/// </summary>
internal static class BmQuery
{
    /// <summary>Words at least this long also match one typo away.</summary>
    private const int FuzzyFrom = 5;

    /// <summary>
    /// Page text: every word must be on the page, as itself, as a Swedish or English stem, or (longer words) one typo
    /// away; every phrase word for word; no excluded word.
    /// </summary>
    public static string Text(SearchQuery query)
    {
        var must = new JsonArray();
        foreach (var term in query.Terms)
        {
            must.Add(
                Should(
                    Match("text_sv", term),
                    Match("text_en", term),
                    Match("text_raw", term, distance: term.Length >= FuzzyFrom ? 1 : null)
                )
            );
        }

        foreach (var phrase in query.Phrases)
        {
            must.Add(
                new JsonObject
                {
                    ["phrase"] = new JsonObject
                    {
                        ["field"] = "text_raw",
                        ["phrases"] = Array(phrase),
                    },
                }
            );
        }

        return Boolean(
            must,
            query.Excluded.Select(t => Should(Match("text_sv", t), Match("text_raw", t)))
        );
    }

    /// <summary>
    /// File and folder names: every word must be in the name, whole (scored highest), as the start of a word, inside a
    /// word ("4471" in "IMG_4471"), or one typo away. Phrases count word by word.
    /// </summary>
    public static string Name(SearchQuery query)
    {
        var must = new JsonArray();
        foreach (var term in query.Terms.Concat(query.Phrases.SelectMany(p => p)).Distinct())
        {
            var options = new List<JsonNode>
            {
                new JsonObject
                {
                    ["boost"] = new JsonObject
                    {
                        ["query"] = new JsonObject
                        {
                            ["term"] = new JsonObject
                            {
                                ["field"] = "name_words",
                                ["value"] = term,
                            },
                        },
                        ["factor"] = 3,
                    },
                },
                FuzzyTerm(term, distance: 0, prefix: true),
            };
            if (term.Length >= FuzzyFrom)
            {
                options.Add(FuzzyTerm(term, distance: 1, prefix: false));
            }

            if (term.Length >= 3)
            {
                options.Add(Match("name_ngram", term, conjunction: true));
            }

            must.Add(Should([.. options]));
        }

        return Boolean(
            must,
            query.Excluded.Select(t =>
                (JsonNode)
                    new JsonObject
                    {
                        ["term"] = new JsonObject { ["field"] = "name_words", ["value"] = t },
                    }
            )
        );
    }

    /// <summary>
    /// A keyword rule (§13.4) on page text and metadata: any entry matches. A word matches as itself, as a Swedish or
    /// English stem, or, from 5 letters, as the start of a longer word ("kvitto" in "kvittot", which the Swedish
    /// stemmer keeps apart); a phrase matches word for word. No typos, unlike search, so a rule doesn't tag files it
    /// shouldn't.
    /// </summary>
    public static string Rule(TagRuleWords words) =>
        new JsonObject
        {
            ["boolean"] = new JsonObject
            {
                ["should"] = new JsonArray([
                    .. words.Entries.Select(entry =>
                        entry.Count == 1
                            ? RuleWord(entry[0])
                            : new JsonObject
                            {
                                ["phrase"] = new JsonObject
                                {
                                    ["field"] = "text_raw",
                                    ["phrases"] = Array(entry),
                                },
                            }
                    ),
                ]),
            },
        }.ToJsonString();

    private static JsonNode RuleWord(string word)
    {
        var options = new List<JsonNode>
        {
            Match("text_sv", word),
            Match("text_en", word),
            Match("text_raw", word),
        };
        if (word.Length >= FuzzyFrom)
        {
            options.Add(FuzzyTerm(word, distance: 0, prefix: true, field: "text_raw"));
        }

        return Should([.. options]);
    }

    private static string Boolean(JsonArray must, IEnumerable<JsonNode> mustNot)
    {
        var boolean = new JsonObject { ["must"] = must };
        var excluded = new JsonArray([.. mustNot]);
        if (excluded.Count > 0)
        {
            boolean["must_not"] = excluded;
        }

        return new JsonObject { ["boolean"] = boolean }.ToJsonString();
    }

    private static JsonNode Should(params JsonNode[] options) =>
        new JsonObject { ["boolean"] = new JsonObject { ["should"] = new JsonArray(options) } };

    private static JsonNode Match(
        string field,
        string value,
        int? distance = null,
        bool conjunction = false
    )
    {
        var match = new JsonObject { ["field"] = field, ["value"] = value };
        if (distance is not null)
        {
            match["distance"] = distance;
        }

        if (conjunction)
        {
            match["conjunction_mode"] = true;
        }

        return new JsonObject { ["match"] = match };
    }

    private static JsonNode FuzzyTerm(
        string value,
        int distance,
        bool prefix,
        string field = "name_words"
    ) =>
        new JsonObject
        {
            ["fuzzy_term"] = new JsonObject
            {
                ["field"] = field,
                ["value"] = value,
                ["distance"] = distance,
                ["prefix"] = prefix,
            },
        };

    private static JsonArray Array(IEnumerable<string> values) =>
        new([.. values.Select(v => (JsonNode)v)]);
}
