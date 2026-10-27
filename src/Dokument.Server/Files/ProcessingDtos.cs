using Dokument.Domain.Jobs;
using Dokument.Infrastructure.Jobs;

namespace Dokument.Server.Files;

/// <summary>A processing step, as the API names it (docs/design.md §10.2).</summary>
public enum JobKind
{
    Thumbnail,
    Metadata,
    Text,
}

public static class JobKinds
{
    public static JobKind Of(JobType type) =>
        type switch
        {
            _ when type == JobType.Thumbnail => JobKind.Thumbnail,
            _ when type == JobType.Metadata => JobKind.Metadata,
            _ when type == JobType.Text => JobKind.Text,
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
            _ => JobType.Text,
        };
}

/// <summary>Where the worker is with one file (the Processing section of the details panel).</summary>
/// <param name="Steps">Thumbnail, metadata and text, in that order.</param>
public sealed record FileProcessing(List<ProcessingStepDto> Steps);

/// <param name="At">When it started (running), finished, or gave up (failed).</param>
/// <param name="Ahead">For a waiting step: how many jobs of its type will run first.</param>
/// <param name="JobId">Admins only: the job, for "Try again" on a failed step.</param>
/// <param name="Attempts">Admins only.</param>
/// <param name="Error">Admins only: why the last attempt failed.</param>
public sealed record ProcessingStepDto(
    JobKind Step,
    StepState State,
    DateTimeOffset? At,
    int? Ahead,
    Guid? JobId,
    int? Attempts,
    string? Error
)
{
    public static ProcessingStepDto From(ProcessingStep step, bool admin) =>
        new(
            JobKinds.Of(step.Type),
            step.State,
            step.At,
            step.Ahead,
            admin ? step.Job?.Id : null,
            admin ? step.Job?.Attempts : null,
            admin ? step.Job?.LastError : null
        );
}
