using Dokument.Domain.Jobs;
using Dokument.Infrastructure.Jobs;

namespace Dokument.Server.Files;

/// <summary>
/// A kind of job, as the API names it: a file's processing steps (docs/design.md §10.2), playable copies (§14),
/// keyword-rule runs (§13.4), then filing a file and a filing rule's "file existing" run (§13.6).
/// </summary>
public enum JobKind
{
    Thumbnail,
    Metadata,
    Text,
    Transcode,
    TagRule,
    File,
    FilingRule,
}

/// <summary>
/// A step every file goes through, as the API names it (docs/design.md §10.2), and the playable copy only video and
/// audio files browsers can't play get (§14).
/// </summary>
public enum FileStep
{
    Thumbnail,
    Metadata,
    Text,
    Transcode,
}

public static class JobKinds
{
    public static FileStep StepOf(JobType type) =>
        type switch
        {
            _ when type == JobType.Thumbnail => FileStep.Thumbnail,
            _ when type == JobType.Metadata => FileStep.Metadata,
            _ when type == JobType.Text => FileStep.Text,
            _ when type == JobType.Transcode => FileStep.Transcode,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not a file step."),
        };

    public static JobKind Of(JobType type) =>
        type switch
        {
            _ when type == JobType.Thumbnail => JobKind.Thumbnail,
            _ when type == JobType.Metadata => JobKind.Metadata,
            _ when type == JobType.Text => JobKind.Text,
            _ when type == JobType.Transcode => JobKind.Transcode,
            _ when type == JobType.TagRule => JobKind.TagRule,
            _ when type == JobType.File => JobKind.File,
            _ when type == JobType.FilingRule => JobKind.FilingRule,
            _ => throw new ArgumentOutOfRangeException(
                nameof(type),
                type,
                "No API name for this job type."
            ),
        };

    /// <summary>Reads a kind as the API writes it ("text"); route and query binding of enums is case-sensitive.</summary>
    public static bool TryParse(string? value, out JobKind kind) =>
        Enum.TryParse(value, ignoreCase: true, out kind) && Enum.IsDefined(kind);

    public static JobType TypeOf(JobKind kind) =>
        kind switch
        {
            JobKind.Thumbnail => JobType.Thumbnail,
            JobKind.Metadata => JobType.Metadata,
            JobKind.Transcode => JobType.Transcode,
            JobKind.TagRule => JobType.TagRule,
            JobKind.File => JobType.File,
            JobKind.FilingRule => JobType.FilingRule,
            _ => JobType.Text,
        };
}

/// <summary>Where the worker is with one file (the Processing section of the details panel).</summary>
/// <param name="Steps">Thumbnail, metadata and text, in that order, then the playable copy for media that needs one.</param>
public sealed record FileProcessing(List<ProcessingStepDto> Steps);

/// <param name="At">When it started (running), finished, or gave up (failed).</param>
/// <param name="Ahead">For a waiting step: how many jobs of its type will run first.</param>
/// <param name="Progress">For a playable copy being made: how far it has got, 0 to 100.</param>
/// <param name="JobId">Admins only: the job, for "Try again" on a failed step.</param>
/// <param name="Attempts">Admins only.</param>
/// <param name="Error">Admins only: why the last attempt failed.</param>
public sealed record ProcessingStepDto(
    FileStep Step,
    StepState State,
    DateTimeOffset? At,
    int? Ahead,
    int? Progress,
    Guid? JobId,
    int? Attempts,
    string? Error
)
{
    public static ProcessingStepDto From(ProcessingStep step, bool admin) =>
        new(
            JobKinds.StepOf(step.Type),
            step.State,
            step.At,
            step.Ahead,
            step.Progress,
            admin ? step.Job?.Id : null,
            admin ? step.Job?.Attempts : null,
            admin ? step.Error ?? step.Job?.LastError : null
        );
}
