namespace Dokument.Domain.Previews;

/// <summary>The images the worker makes from a file (docs/design.md §10.3). All are WebP.</summary>
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

    /// <summary>The name used in the database and in file names, as in §6: <c>thumb_s</c>, <c>thumb_m</c>, <c>preview</c>.</summary>
    public static string Key(this RenditionKind kind) =>
        kind switch
        {
            RenditionKind.ThumbSmall => "thumb_s",
            RenditionKind.ThumbMedium => "thumb_m",
            RenditionKind.Preview => "preview",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    public static RenditionKind FromKey(string key) =>
        key switch
        {
            "thumb_s" => RenditionKind.ThumbSmall,
            "thumb_m" => RenditionKind.ThumbMedium,
            "preview" => RenditionKind.Preview,
            _ => throw new ArgumentException($"\"{key}\" isn't a rendition kind.", nameof(key)),
        };
}
