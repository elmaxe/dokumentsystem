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

    /// <summary>Runs a person's filing rules over files they already have, when they ask for it (§13.6). The subject is the run's id.</summary>
    public static readonly JobType FilingRun = new("filing-run");

    /// <summary>A playable copy of a video or audio blob the browser can't play as it is (§10.2, §14).</summary>
    public static readonly JobType Transcode = new("transcode");

    /// <summary>The frames a video's timeline shows while seeking, as one sheet (§10.2, §14).</summary>
    public static readonly JobType SeekPreviews = new("seek-previews");

    /// <summary>Speech to text for a video or audio blob, through the ML service (§11.1).</summary>
    public static readonly JobType Transcribe = new("transcribe");

    /// <summary>Every page of a PDF as small images, for the split screen (§8.4). The subject is the blob's hash.</summary>
    public static readonly JobType PageSheets = new("page-sheets");

    /// <summary>Puts the pages of a merge or split together with PDFium (§8.4). The subject is the operation's id.</summary>
    public static readonly JobType Pdf = new("pdf");

    /// <summary>
    /// Adds the PDFs a <see cref="Pdf"/> job made to the drive (§8.4). Run by the server, not the worker, since only the
    /// server may write originals (§10.4). The subject is the operation's id.
    /// </summary>
    public static readonly JobType PdfSave = new("pdf-save");

    /// <summary>The steps every file goes through, in pipeline order (§10.2).</summary>
    public static readonly IReadOnlyList<JobType> FileSteps = [Thumbnail, Metadata, Text];

    /// <summary>The types the worker runs, in the order the admin Jobs page shows them (§10.1).</summary>
    public static readonly IReadOnlyList<JobType> All =
    [
        .. FileSteps,
        Transcode,
        SeekPreviews,
        Transcribe,
        PageSheets,
        Pdf,
        PdfSave,
        TagRule,
        File,
        FilingRun,
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
