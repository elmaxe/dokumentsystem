using System.Globalization;
using System.Text.Json;
using Dokument.Domain.Search;

namespace Dokument.Infrastructure.Search;

/// <summary>A detail shown read-only in the details panel, by a key the clients translate ("author", "album").</summary>
public sealed record DetailField(string Key, string Value);

/// <summary>
/// What the metadata stage read from a file's bytes (docs/design.md §10.3), as the details panel shows it (§13.5): exif
/// tags folded into a title, a description, the date taken and a few named fields, in a fixed order.
/// </summary>
public sealed record ReadDetails(
    string? Title,
    string? Description,
    PartialDate? Taken,
    int? TakenOffsetMinutes,
    string? Lens,
    int? Width,
    int? Height,
    IReadOnlyList<DetailField> Fields
)
{
    public static ReadDetails None { get; } = new(null, null, null, null, null, null, null, []);

    /// <summary>Each field and the tags it is read from, first found wins; in the order the panel shows them.</summary>
    private static readonly (string Key, string[] Tags)[] FieldTags =
    [
        ("subject", ["Subject", "XPSubject"]),
        ("author", ["Author", "Creator"]),
        ("artist", ["Artist", "AlbumArtist", "Band"]),
        ("composer", ["Composer"]),
        ("album", ["Album"]),
        ("genre", ["Genre"]),
        ("company", ["Company"]),
        ("keywords", ["Keywords", "XPKeywords", "Category"]),
        ("comment", ["Comment", "XPComment", "UserComment"]),
    ];

    public static ReadDetails From(BlobMetadata? metadata)
    {
        if (metadata is null)
        {
            return None;
        }

        var tags =
            JsonSerializer.Deserialize<Dictionary<string, string>>(metadata.Tags)
            ?? new Dictionary<string, string>();
        string? First(params string[] names) =>
            names.Select(tags.GetValueOrDefault).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        var title = First("Title", "XPTitle", "Headline");
        var description = First("Description", "ImageDescription", "Caption-Abstract");
        var shown = new HashSet<string?>(StringComparer.OrdinalIgnoreCase) { title, description };
        var fields = new List<DetailField>();
        foreach (var (key, names) in FieldTags)
        {
            if (First(names) is { } value && shown.Add(value))
            {
                fields.Add(new DetailField(key, value));
            }
        }

        return new ReadDetails(
            title,
            description,
            metadata.TakenAt is { } at
                ? PartialDate.From(
                    new DateTimeOffset(
                        FileDetailsService.Local(at, metadata.TakenOffsetMinutes),
                        TimeSpan.Zero
                    ),
                    DatePrecision.Time
                )
                : null,
            metadata.TakenOffsetMinutes,
            First("LensModel"),
            Number(First("ImageWidth")),
            Number(First("ImageHeight")),
            fields
        );
    }

    private static int? Number(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
            ? n
            : null;
}
