namespace Dokument.Domain.Previews;

/// <summary>What the worker makes from a file (docs/design.md §10.3): WebP images, and copies the viewer can show.</summary>
public enum RenditionKind
{
    /// <summary>For list rows and small tiles: at most 256 px on the long edge.</summary>
    ThumbSmall,

    /// <summary>For the file grid's 4:3 tiles: at most 640 px on the long edge.</summary>
    ThumbMedium,

    /// <summary>
    /// What the viewer shows for images a browser can't display (HEIC, TIFF) or that are very large: at most 2048 px.
    /// Other images are shown from the original.
    /// </summary>
    Preview,

    /// <summary>
    /// A video or audio file browsers can play (H.264/AAC in MP4), made only when the original isn't playable. Not an
    /// image, so it has no <see cref="RenditionKinds.MaxEdge"/>.
    /// </summary>
    Playable,

    /// <summary>A PDF of an Office or OpenDocument file, made by Gotenberg, that the viewer shows with pdf.js.</summary>
    Document,

    /// <summary>
    /// An audio file's loudness over time, for the player's waveform (§14): <see cref="Waveform"/>'s binary format.
    /// Not an image.
    /// </summary>
    Waveform,
}

public static class RenditionKinds
{
    /// <summary>The longest edge in pixels. Smaller sources are never scaled up.</summary>
    public static int MaxEdge(this RenditionKind kind) =>
        kind switch
        {
            RenditionKind.ThumbSmall => 256,
            RenditionKind.ThumbMedium => 640,
            RenditionKind.Preview => 2048,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    /// <summary>The name used in the database and in file names, as in §6: <c>thumb_s</c>, <c>thumb_m</c>, <c>preview</c>, <c>playable</c>, <c>document</c>, <c>waveform</c>.</summary>
    public static string Key(this RenditionKind kind) =>
        kind switch
        {
            RenditionKind.ThumbSmall => "thumb_s",
            RenditionKind.ThumbMedium => "thumb_m",
            RenditionKind.Preview => "preview",
            RenditionKind.Playable => "playable",
            RenditionKind.Document => "document",
            RenditionKind.Waveform => "waveform",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    public static RenditionKind FromKey(string key) =>
        key switch
        {
            "thumb_s" => RenditionKind.ThumbSmall,
            "thumb_m" => RenditionKind.ThumbMedium,
            "preview" => RenditionKind.Preview,
            "playable" => RenditionKind.Playable,
            "document" => RenditionKind.Document,
            "waveform" => RenditionKind.Waveform,
            _ => throw new ArgumentException($"\"{key}\" isn't a rendition kind.", nameof(key)),
        };
}
