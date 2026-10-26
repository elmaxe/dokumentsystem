namespace Dokument.Domain.Jobs;

/// <summary>
/// Thrown by a job handler that can't run yet for a reason outside the file, such as no ML service answering: the job
/// waits <see cref="Delay"/> and the attempt doesn't count (<see cref="Job.Postpone"/>).
/// </summary>
public sealed class JobPostponedException(string reason, TimeSpan delay) : Exception(reason)
{
    public TimeSpan Delay { get; } = delay;
}
