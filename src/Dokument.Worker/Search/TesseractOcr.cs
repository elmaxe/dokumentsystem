using System.Globalization;
using System.Text;
using System.Text.Json;
using Dokument.Worker.Jobs;
using Microsoft.Extensions.Options;

namespace Dokument.Worker.Search;

/// <summary>One recognized word and where it is, each number a fraction of the image's size.</summary>
public sealed record OcrWord(string Text, double X, double Y, double Width, double Height);

/// <summary>Recognized text, line by line, and its words with their positions.</summary>
public sealed record OcrResult(string Text, IReadOnlyList<OcrWord> Words)
{
    public static OcrResult Empty { get; } = new("", []);

    /// <summary>The positions as stored on a content page: <c>[[x, y, w, h, "word"], …]</c>, rounded to keep them small.</summary>
    public string WordsJson()
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartArray();
            foreach (var word in Words)
            {
                json.WriteStartArray();
                json.WriteNumberValue(Math.Round(word.X, 4));
                json.WriteNumberValue(Math.Round(word.Y, 4));
                json.WriteNumberValue(Math.Round(word.Width, 4));
                json.WriteNumberValue(Math.Round(word.Height, 4));
                json.WriteStringValue(word.Text);
                json.WriteEndArray();
            }

            json.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

/// <summary>
/// Reads text in scans and photos (docs/design.md §10.2, §11). Tesseract ships in the image, so a server without an ML
/// service still gets searchable scans; the ML service's <c>/ocr</c> takes over in M7 behind the same interface.
/// </summary>
public interface ITextRecognizer
{
    /// <summary>The engine and its version, stored with the results so a better engine reads everything again.</summary>
    Task<string> EngineAsync(CancellationToken ct);

    /// <summary>Recognizes the text in an 8-bit grey PNG.</summary>
    Task<OcrResult> RecognizeAsync(byte[] png, int width, int height, CancellationToken ct);
}

/// <summary>Runs the <c>tesseract</c> command with the configured languages (Swedish and English by default, §23).</summary>
public sealed class TesseractOcr(IOptions<WorkerOptions> options) : ITextRecognizer
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Words Tesseract is less sure of than this are left out. Photos without text give a scatter of low-confidence
    /// "words", which would only add noise to search.
    /// </summary>
    internal const int MinConfidence = 55;

    /// <summary>Fewer kept words than this and the image is taken to have no text, unless one of them is a sure word.</summary>
    internal const int MinWords = 3;

    /// <summary>
    /// A word of at least <see cref="SureLetters"/> letters read this confidently counts on its own, so a title or sign
    /// with one or two words isn't lost. Noise from photos rarely gets this sure.
    /// </summary>
    internal const int SureConfidence = 85;

    internal const int SureLetters = 4;

    private string? engine;

    public async Task<string> EngineAsync(CancellationToken ct)
    {
        if (engine is null)
        {
            var output = await ExternalTool.RunAsync(
                options.Value.TesseractCommand,
                ["--version"],
                TimeSpan.FromSeconds(30),
                ct
            );
            var text = Encoding.UTF8.GetString(output.Stdout) + output.Stderr;
            engine =
                text.Split('\n', StringSplitOptions.TrimEntries)
                    .FirstOrDefault(l =>
                        l.StartsWith("tesseract", StringComparison.OrdinalIgnoreCase)
                    )
                ?? "tesseract";
        }

        return engine;
    }

    public async Task<OcrResult> RecognizeAsync(
        byte[] png,
        int width,
        int height,
        CancellationToken ct
    )
    {
        var output = await ExternalTool.RunAsync(
            options.Value.TesseractCommand,
            ["stdin", "stdout", "-l", options.Value.OcrLanguages, "--psm", "3", "tsv"],
            Timeout,
            ct,
            stdin: png
        );
        if (output.ExitCode != 0)
        {
            throw new InvalidOperationException($"Tesseract failed: {output.Stderr.Trim()}");
        }

        return ParseTsv(Encoding.UTF8.GetString(output.Stdout), width, height);
    }

    /// <summary>
    /// Tesseract's TSV has one row per word (level 5) with its block, paragraph and line numbers, its box in pixels and
    /// its confidence. Words become lines and paragraphs again; the boxes become fractions of the image.
    /// </summary>
    internal static OcrResult ParseTsv(string tsv, int width, int height)
    {
        var text = new StringBuilder();
        var words = new List<OcrWord>();
        (string Block, string Paragraph, string Line)? last = null;
        var sure = false;
        foreach (var row in tsv.Split('\n'))
        {
            var cells = row.TrimEnd('\r').Split('\t');
            if (cells.Length < 12 || cells[0] != "5")
            {
                continue;
            }

            var word = cells[11].Trim();
            if (
                word.Length == 0
                || !word.Any(char.IsLetterOrDigit)
                || !double.TryParse(cells[10], CultureInfo.InvariantCulture, out var confidence)
                || confidence < MinConfidence
            )
            {
                continue;
            }

            sure |= confidence >= SureConfidence && word.Count(char.IsLetter) >= SureLetters;
            var position = (cells[2], cells[3], cells[4]);
            if (last is { } previous)
            {
                text.Append(
                    previous.Block != position.Item1 || previous.Paragraph != position.Item2
                        ? "\n\n"
                    : previous.Line != position.Item3 ? "\n"
                    : " "
                );
            }

            text.Append(word);
            last = position;
            if (
                width > 0
                && height > 0
                && int.TryParse(cells[6], CultureInfo.InvariantCulture, out var left)
                && int.TryParse(cells[7], CultureInfo.InvariantCulture, out var top)
                && int.TryParse(cells[8], CultureInfo.InvariantCulture, out var w)
                && int.TryParse(cells[9], CultureInfo.InvariantCulture, out var h)
            )
            {
                words.Add(
                    new OcrWord(
                        word,
                        (double)left / width,
                        (double)top / height,
                        (double)w / width,
                        (double)h / height
                    )
                );
            }
        }

        return words.Count < MinWords && !sure
            ? OcrResult.Empty
            : new OcrResult(text.ToString(), words);
    }
}
