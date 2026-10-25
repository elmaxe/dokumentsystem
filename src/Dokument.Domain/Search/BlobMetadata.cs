using Dokument.Domain.Files;

namespace Dokument.Domain.Search;

/// <summary>
/// What the metadata stage (docs/design.md §10.2) read from a blob with exiftool: the fields search filters on, plus
/// every tag it kept, for the details panel. Like thumbnails and text, it belongs to the bytes. The original is never
/// changed (§1).
/// </summary>
public sealed class BlobMetadata
{
    /// <summary>Bump when more tags are read, so every blob is read again in the background.</summary>
    public const int CurrentPipelineVersion = 1;

    public required ContentHash BlobHash { get; init; }
    public int PipelineVersion { get; set; } = CurrentPipelineVersion;

    /// <summary>When a photo or video was taken, from the camera's clock. Null when the file doesn't say.</summary>
    public DateTimeOffset? TakenAt { get; set; }

    /// <summary>"Make Model", e.g. "FUJIFILM X-T30".</summary>
    public string? Camera { get; set; }

    public double? DurationSeconds { get; set; }

    /// <summary>The kept tags as a JSON object of name to text, e.g. <c>{"Title": "…", "Artist": "…"}</c>.</summary>
    public string Tags { get; set; } = "{}";

    /// <summary>False when exiftool couldn't read the file at all.</summary>
    public bool Readable { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; }
}
