using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dokument.Worker.Search;

/// <summary>What exiftool found in a file. <see cref="SearchText"/> is what search matches; empty when nothing useful was found.</summary>
public sealed record MetadataResult(
    bool Readable,
    DateTimeOffset? TakenAt,
    string? Camera,
    double? DurationSeconds,
    IReadOnlyDictionary<string, string> Tags,
    string SearchText
)
{
    public static MetadataResult Unreadable { get; } =
        new(false, null, null, null, new Dictionary<string, string>(), "");
}

/// <summary>
/// Reads metadata with exiftool (docs/design.md §10.3): camera and date taken from photos and videos, title and author
/// from PDF and Office files, and tags from music. Only a fixed list of tags is asked for, so nothing private such as a
/// serial number is kept. The original is opened read-only.
/// </summary>
public sealed partial class ExifTool
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);

    /// <summary>Tags whose text is searchable, in the order the details panel would show them.</summary>
    internal static readonly string[] TextTags =
    [
        "Title",
        "XPTitle",
        "Headline",
        "Subject",
        "XPSubject",
        "Description",
        "ImageDescription",
        "Caption-Abstract",
        "Keywords",
        "XPKeywords",
        "Category",
        "Author",
        "Creator",
        "Artist",
        "AlbumArtist",
        "Band",
        "Composer",
        "Album",
        "Genre",
        "Company",
        "Comment",
        "XPComment",
        "UserComment",
        "Make",
        "Model",
        "LensModel",
    ];

    /// <summary>Tags read for the filters, not matched as text.</summary>
    private static readonly string[] OtherTags =
    [
        "DateTimeOriginal",
        "OffsetTimeOriginal",
        "CreateDate",
        "MediaCreateDate",
        "Duration",
    ];

    /// <summary>The longest value kept per tag. Comments can hold whole documents.</summary>
    private const int MaxValueLength = 4000;

    public async Task<MetadataResult> ReadAsync(string path, CancellationToken ct)
    {
        var arguments = new List<string>
        {
            "-json",
            "-n",
            "-fast",
            "-charset",
            "filename=utf8",
            "-api",
            "largefilesupport=1",
        };
        arguments.AddRange(TextTags.Concat(OtherTags).Select(t => "-" + t));
        arguments.Add("--");
        arguments.Add(path);
        var output = await ExternalTool.RunAsync("exiftool", arguments, Timeout, ct);

        // exiftool exits with 1 when a file has nothing it can read, but still prints JSON.
        return output.Stdout.Length == 0 ? MetadataResult.Unreadable : Parse(output.Stdout);
    }

    internal static MetadataResult Parse(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        if (
            document.RootElement.ValueKind != JsonValueKind.Array
            || document.RootElement.GetArrayLength() == 0
        )
        {
            return MetadataResult.Unreadable;
        }

        var tags = new Dictionary<string, string>();
        foreach (var property in document.RootElement[0].EnumerateObject())
        {
            if (property.Name == "SourceFile" || ValueOf(property.Value) is not { } value)
            {
                continue;
            }

            tags[property.Name] = value.Length > MaxValueLength ? value[..MaxValueLength] : value;
        }

        var camera = CameraOf(tags.GetValueOrDefault("Make"), tags.GetValueOrDefault("Model"));
        var takenAt =
            DateOf(
                tags.GetValueOrDefault("DateTimeOriginal"),
                tags.GetValueOrDefault("OffsetTimeOriginal")
            )
            ?? DateOf(tags.GetValueOrDefault("CreateDate"), null)
            ?? DateOf(tags.GetValueOrDefault("MediaCreateDate"), "+00:00");
        double? duration =
            double.TryParse(
                tags.GetValueOrDefault("Duration"),
                CultureInfo.InvariantCulture,
                out var seconds
            )
            && seconds > 0
                ? seconds
                : null;

        // What a person might type: the camera as one phrase, then the text tags, each value once.
        var text = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (
            var value in TextTags
                .Where(t => t is not ("Make" or "Model"))
                .Select(tags.GetValueOrDefault)
                .Prepend(camera)
        )
        {
            if (!string.IsNullOrWhiteSpace(value) && seen.Add(value))
            {
                text.AppendLine(value);
            }
        }

        var kept = tags.Where(t => TextTags.Contains(t.Key) || OtherTags.Contains(t.Key))
            .ToDictionary(t => t.Key, t => t.Value);
        return new MetadataResult(true, takenAt, camera, duration, kept, text.ToString().Trim());
    }

    private static string? ValueOf(JsonElement value)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.Array => string.Join(
                ", ",
                value.EnumerateArray().Select(ValueOf).Where(v => !string.IsNullOrWhiteSpace(v))
            ),
            _ => null,
        };
        text = text?.Replace('\0', ' ').Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>"Canon" + "Canon EOS R5" → "Canon EOS R5"; "FUJIFILM" + "X-T30" → "FUJIFILM X-T30".</summary>
    internal static string? CameraOf(string? make, string? model)
    {
        make = make?.Trim();
        model = model?.Trim();
        if (string.IsNullOrEmpty(model))
        {
            return string.IsNullOrEmpty(make) ? null : make;
        }

        if (string.IsNullOrEmpty(make))
        {
            return model;
        }

        var brand = make.Split(' ')[0];
        return model.StartsWith(brand, StringComparison.OrdinalIgnoreCase)
            ? model
            : $"{make} {model}";
    }

    /// <summary>
    /// exiftool's "2024:03:14 15:42:01", with the camera's UTC offset when it recorded one. Without one, the camera's
    /// clock is kept as if it were UTC, so "taken in 2024" means the year the camera showed.
    /// </summary>
    internal static DateTimeOffset? DateOf(string? value, string? offset)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("0000", StringComparison.Ordinal))
        {
            return null;
        }

        // Some files add their own offset or fractions: "2024:03:14 15:42:01.123+02:00".
        if (value.Length < 19)
        {
            return null;
        }

        var core = value[..19];
        if (
            !DateTime.TryParseExact(
                core,
                "yyyy:MM:dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var local
            )
        )
        {
            return null;
        }

        // The offset, given separately or after the time and any fraction of a second.
        var zone = offset ?? Offset().Match(value[19..]).Value;
        var match = Offset().Match(zone);
        if (!match.Success)
        {
            return new DateTimeOffset(local, TimeSpan.Zero);
        }

        var span = new TimeSpan(
            int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture),
            0
        );
        return span <= TimeSpan.FromHours(14)
            ? new DateTimeOffset(local, match.Groups["sign"].Value == "-" ? -span : span)
            : new DateTimeOffset(local, TimeSpan.Zero);
    }

    [GeneratedRegex(@"(?<sign>[+-])(?<h>\d{2}):(?<m>\d{2})")]
    private static partial Regex Offset();
}
