using Dokument.Domain.Jobs;
using Dokument.Infrastructure.Jobs;
using Dokument.Server.Files;

namespace Dokument.Server.Admin;

/// <summary>One queue on the Jobs page (docs/design.md §10.1).</summary>
/// <param name="DoneLastWeek">Jobs finished in the last 7 days.</param>
/// <param name="Paused">Workers don't start its jobs; running ones finish.</param>
public sealed record JobQueueDto(
    JobKind Type,
    int Running,
    int Waiting,
    int Failed,
    int DoneLastWeek,
    bool Paused
);

public sealed record PauseQueueRequest(bool Paused);

/// <param name="More">Whether there are more jobs after these.</param>
public sealed record JobList(List<JobDto> Items, bool More);

/// <param name="StartedAt">When the current or last attempt started.</param>
/// <param name="FinishedAt">For failed jobs: when they gave up.</param>
/// <param name="RunAfter">For queued jobs: the earliest they run, later than now while a retry backs off.</param>
/// <param name="Error">Why the last attempt failed. A job that is retrying keeps it until it succeeds.</param>
/// <param name="Size">Bytes of the content, from the first file that uses it.</param>
/// <param name="Files">
/// The files that use the content, best match first: current versions, then trashed files, then older versions. Empty
/// when no file uses it any more, and for keyword-rule runs.
/// </param>
/// <param name="RuleTag">
/// For a keyword-rule run (<c>tagRule</c>) or a filing run of one rule (<c>filingRun</c>): the tag of the rule it runs.
/// Null once the rule is deleted, and for a filing run of all of a person's rules.
/// </param>
public sealed record JobDto(
    Guid Id,
    JobKind Type,
    JobStatus Status,
    JobPriority Priority,
    int Attempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    DateTimeOffset RunAfter,
    string? Error,
    long? Size,
    string? MimeType,
    List<JobFileDto> Files,
    string? RuleTag
)
{
    public static JobDto From(JobEntry entry) =>
        new(
            entry.Job.Id,
            JobKinds.Of(entry.Job.Type),
            entry.Job.Status,
            entry.Job.Priority,
            entry.Job.Attempts,
            entry.Job.CreatedAt,
            entry.Job.StartedAt,
            entry.Job.FinishedAt,
            entry.Job.RunAfter,
            entry.Job.LastError,
            entry.Files.FirstOrDefault()?.Version.Size,
            entry.Files.FirstOrDefault()?.Version.MimeType,
            entry.Files.Select(JobFileDto.From).ToList(),
            entry.RuleTag
        );
}

/// <param name="Path">The folders above the file, top first.</param>
/// <param name="VersionNumber">Which version of the file has the content.</param>
public sealed record JobFileDto(
    Guid NodeId,
    string Name,
    Guid OwnerId,
    string OwnerName,
    JobFileUse Use,
    int VersionNumber,
    List<PathEntry> Path
)
{
    public static JobFileDto From(JobFile file) =>
        new(
            file.Node.Id,
            file.Node.Name.Value,
            file.Node.OwnerId,
            file.OwnerName,
            file.Use,
            file.Version.Number,
            file.Folders.Select(f => new PathEntry(f.Id, f.Name.Value)).ToList()
        );
}
