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

    /// <summary>Runs one keyword rule over the stored text of every blob, after an admin changed it (§13.4).</summary>
    public static readonly JobType TagRule = new("tag-rule");

    /// <summary>Runs its owner's filing rules on one file, after it got a tag or new bytes (§13.6). The subject is the node id.</summary>
    public static readonly JobType File = new("file");

    /// <summary>Files everything that has a filing rule's tag now, when the person asks for it (§13.6).</summary>
    public static readonly JobType FilingRule = new("filing-rule");

    /// <summary>A playable copy of a video or audio blob the browser can't play as it is (§10.2, §14).</summary>
    public static readonly JobType Transcode = new("transcode");

    /// <summary>The steps every file goes through, in pipeline order (§10.2).</summary>
    public static readonly IReadOnlyList<JobType> FileSteps = [Thumbnail, Metadata, Text];

    /// <summary>The types the worker runs, in the order the admin Jobs page shows them (§10.1).</summary>
    public static readonly IReadOnlyList<JobType> All =
    [
        .. FileSteps,
        Transcode,
        TagRule,
        File,
        FilingRule,
    ];

    private JobType(string name) => Name = name;

    public string Name { get; }

    public static JobType From(string name) =>
        name.Length is > 0 and <= 64
        && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.')
            ? new JobType(name)
            : throw new ArgumentException($"\"{name}\" isn't a job type name.", nameof(name));

    public override string ToString() => Name;
}
