using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dokument.Domain.Files;

namespace Dokument.Domain.Search;

public enum TranscriptStatus
{
    /// <summary>Started and stopped part way, e.g. the worker restarted; the next run resumes at <see cref="BlobTranscript.DoneMs"/>.</summary>
    Partial,

    /// <summary>The whole recording is transcribed; see the blob's transcript pages.</summary>
    Ready,

    /// <summary>Under a few seconds of speech: music, silence, a pocket recording. Not tried again.</summary>
    NoSpeech,

    /// <summary>The audio couldn't be read, e.g. a damaged file. Not tried again until an admin asks.</summary>
    Failed,
}

/// <summary>
/// Speech to text for a video or audio blob (docs/design.md §11.1). Like other text it belongs to the bytes, so a
/// recording is transcribed once whoever has a copy. The text itself is in the blob's <see cref="ContentPage"/>s with
/// <see cref="PageSource.Transcript"/>.
/// </summary>
public sealed class BlobTranscript
{
    public required ContentHash BlobHash { get; init; }
    public required TranscriptStatus Status { get; set; }

    /// <summary>The spoken language, <c>sv</c> or <c>en</c>, as detected (or as resumed with).</summary>
    public string? Language { get; set; }

    /// <summary>The model, e.g. <c>large-v3-turbo</c> or <c>kb-whisper-small</c>, and the engine with its version.</summary>
    public string? Model { get; set; }
    public string? Engine { get; set; }
    public int? DurationMs { get; set; }

    /// <summary>How far the stored pages reach, so an interrupted run resumes there instead of starting over.</summary>
    public int DoneMs { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A word as heard, with its time.</summary>
[JsonConverter(typeof(TranscriptWordConverter))]
public sealed record TranscriptWord(int StartMs, int EndMs, string Text);

/// <summary>One stretch of speech as the ML service returned it: a sentence or so.</summary>
public sealed record TranscriptLine(
    [property: JsonPropertyName("s")] int StartMs,
    [property: JsonPropertyName("e")] int EndMs,
    [property: JsonPropertyName("t")] string Text,
    [property: JsonPropertyName("w")] IReadOnlyList<TranscriptWord> Words
)
{
    // Stored, never put in a page as is, so å, ä and ö can stay themselves.
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>A transcript page's lines, as kept in <see cref="ContentPage.Words"/>.</summary>
    public static string Serialize(IEnumerable<TranscriptLine> lines) =>
        JsonSerializer.Serialize(lines.ToList(), Json);

    public static List<TranscriptLine> Deserialize(string json) =>
        JsonSerializer.Deserialize<List<TranscriptLine>>(json, Json) ?? [];
}

/// <summary>A transcript page in the making: the lines of about a minute of speech.</summary>
public sealed record TranscriptPage(IReadOnlyList<TranscriptLine> Lines)
{
    public int StartMs => Lines[0].StartMs;
    public int EndMs => Lines[^1].EndMs;
    public string Text => string.Join(' ', Lines.Select(l => l.Text));
}

/// <summary>
/// Groups lines into pages of about a minute, cut at a pause, so a search hit names a moment rather than the whole
/// recording and a page's snippet stays readable. A page is complete once the next line shows it should end there.
/// </summary>
public sealed class TranscriptPager
{
    public static readonly TimeSpan Target = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan Longest = TimeSpan.FromSeconds(90);

    /// <summary>A pause at least this long ends a page that has reached <see cref="Target"/>.</summary>
    public static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(700);

    public const int MaxChars = 2000;

    private readonly List<TranscriptLine> lines = [];
    private int chars;

    /// <summary>Adds a line; returns the page it completes, if it starts a new one.</summary>
    public TranscriptPage? Add(TranscriptLine line)
    {
        TranscriptPage? done = null;
        if (lines.Count > 0 && Breaks(line))
        {
            done = Flush();
        }

        lines.Add(line);
        chars += line.Text.Length + 1;
        return done;
    }

    /// <summary>The last page, at the end of the recording.</summary>
    public TranscriptPage? Flush()
    {
        if (lines.Count == 0)
        {
            return null;
        }

        var page = new TranscriptPage([.. lines]);
        lines.Clear();
        chars = 0;
        return page;
    }

    private bool Breaks(TranscriptLine next)
    {
        var start = lines[0].StartMs;
        var span = TimeSpan.FromMilliseconds(lines[^1].EndMs - start);
        var gap = TimeSpan.FromMilliseconds(next.StartMs - lines[^1].EndMs);
        return (span >= Target && gap >= Pause)
            || TimeSpan.FromMilliseconds(next.EndMs - start) > Longest
            || chars + next.Text.Length > MaxChars;
    }
}

/// <summary>Captions for a video from its transcript (§11.1): WebVTT, at most two short lines on screen at a time.</summary>
public static class WebVtt
{
    /// <summary>Characters per caption line, as subtitle guides suggest.</summary>
    public const int LineLength = 42;

    private static readonly TimeSpan LongestCue = TimeSpan.FromSeconds(7);

    public static string From(IEnumerable<TranscriptLine> lines)
    {
        var vtt = new StringBuilder("WEBVTT\n");
        foreach (var (start, end, text) in lines.SelectMany(Cues))
        {
            vtt.Append('\n')
                .Append(Time(start))
                .Append(" --> ")
                .Append(Time(end))
                .Append('\n')
                .Append(Wrap(text))
                .Append('\n');
        }

        return vtt.ToString();
    }

    /// <summary>A line as one cue, or split at words when it's too long to read at once.</summary>
    public static IEnumerable<(int Start, int End, string Text)> Cues(TranscriptLine line)
    {
        var text = Clean(line.Text);
        if (
            line.Words.Count == 0
            || (
                text.Length <= LineLength * 2
                && TimeSpan.FromMilliseconds(line.EndMs - line.StartMs) <= LongestCue
            )
        )
        {
            yield return (line.StartMs, Math.Max(line.EndMs, line.StartMs + 1), text);
            yield break;
        }

        var cue = new List<TranscriptWord>();
        foreach (var word in line.Words)
        {
            var length = cue.Sum(w => w.Text.Length + 1) + word.Text.Length;
            if (
                cue.Count > 0
                && (
                    length > LineLength * 2
                    || TimeSpan.FromMilliseconds(word.EndMs - cue[0].StartMs) > LongestCue
                )
            )
            {
                yield return Join(cue);
                cue.Clear();
            }

            cue.Add(word);
        }

        if (cue.Count > 0)
        {
            yield return Join(cue);
        }
    }

    private static (int, int, string) Join(List<TranscriptWord> words) =>
        (
            words[0].StartMs,
            Math.Max(words[^1].EndMs, words[0].StartMs + 1),
            Clean(string.Join(' ', words.Select(w => w.Text)))
        );

    /// <summary>Breaks a cue into two lines near the middle when it's longer than one.</summary>
    private static string Wrap(string text)
    {
        if (text.Length <= LineLength)
        {
            return text;
        }

        var middle = text.Length / 2;
        var before = text.LastIndexOf(' ', middle);
        var after = text.IndexOf(' ', middle);
        var at =
            before < 0 ? after
            : after < 0 ? before
            : middle - before <= after - middle ? before
            : after;
        return at < 0 ? text : $"{text[..at]}\n{text[(at + 1)..]}";
    }

    /// <summary>A cue can't hold a blank line or "-->", and its text is plain, not markup.</summary>
    private static string Clean(string text) =>
        string.Join(
                ' ',
                text.Split((char[])['\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            )
            .Replace("-->", "->", StringComparison.Ordinal)
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Trim();

    private static string Time(int ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}"
        );
    }
}

/// <summary>Reads and writes a word as <c>[start_ms, end_ms, "word"]</c>, the ML service's shape.</summary>
internal sealed class TranscriptWordConverter : JsonConverter<TranscriptWord>
{
    public override TranscriptWord Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("A word is [start_ms, end_ms, \"word\"].");
        }

        reader.Read();
        var start = reader.GetInt32();
        reader.Read();
        var end = reader.GetInt32();
        reader.Read();
        var text = reader.GetString() ?? "";
        reader.Read();
        if (reader.TokenType != JsonTokenType.EndArray)
        {
            throw new JsonException("A word is [start_ms, end_ms, \"word\"].");
        }

        return new TranscriptWord(start, end, text);
    }

    public override void Write(
        Utf8JsonWriter writer,
        TranscriptWord value,
        JsonSerializerOptions options
    )
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.StartMs);
        writer.WriteNumberValue(value.EndMs);
        writer.WriteStringValue(value.Text);
        writer.WriteEndArray();
    }
}
