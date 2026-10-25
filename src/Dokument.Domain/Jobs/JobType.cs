namespace Dokument.Domain.Jobs;

/// <summary>
/// A kind of background job (docs/design.md §10.1), stored as its name. Each type has its own handler and its own
/// concurrency limit in the worker.
/// </summary>
public sealed record JobType
{
    /// <summary>Thumbnails and preview renditions for one blob (§10.2).</summary>
    public static readonly JobType Thumbnail = new("thumbnail");

    /// <summary>Metadata such as the camera, the date taken or a song's artist, for one blob (§10.2).</summary>
    public static readonly JobType Metadata = new("metadata");

    /// <summary>Searchable text for one blob: the text layer, then recognition for scans and photos (§10.2, §13.2).</summary>
    public static readonly JobType Text = new("text");

    private JobType(string name) => Name = name;

    public string Name { get; }

    public static JobType From(string name) =>
        name.Length is > 0 and <= 64
        && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.')
            ? new JobType(name)
            : throw new ArgumentException($"\"{name}\" isn't a job type name.", nameof(name));

    public override string ToString() => Name;
}
