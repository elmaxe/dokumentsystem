using Dokument.Domain.Jobs;

namespace Dokument.Worker.Jobs;

/// <summary>Does the work for one <see cref="JobType"/>. Resolved in a fresh scope per job. Must be idempotent (docs/design.md §10.1).</summary>
public interface IJobHandler
{
    Task RunAsync(Job job, CancellationToken ct);
}

/// <param name="Concurrency">How many jobs of this type one worker runs at once. NAS-friendly by default (§10.1).</param>
/// <param name="Timeout">How long one job may run; null for the worker's default.</param>
public sealed record JobHandlerRegistration(
    JobType Type,
    Type HandlerType,
    int Concurrency,
    TimeSpan? Timeout = null
);
