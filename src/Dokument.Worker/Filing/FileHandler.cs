using Dokument.Domain.Jobs;
using Dokument.Infrastructure.Filing;
using Dokument.Worker.Jobs;

namespace Dokument.Worker.Filing;

/// <summary>Runs its owner's filing rules on one file, after it got a tag or new bytes (docs/design.md §13.6).</summary>
internal sealed class FileHandler(Filer filer) : IJobHandler
{
    public Task RunAsync(Job job, CancellationToken ct) =>
        Guid.TryParse(job.Subject, out var id)
            ? filer.FileAsync(id, ct)
            : throw new InvalidOperationException($"\"{job.Subject}\" isn't a node id.");
}

/// <summary>Runs a person's filing rules over files they already have, when they asked for it (docs/design.md §13.6).</summary>
internal sealed class FilingRunHandler(FilingService filing) : IJobHandler
{
    public Task RunAsync(Job job, CancellationToken ct) =>
        Guid.TryParse(job.Subject, out var id)
            ? filing.RunAsync(id, ct)
            : throw new InvalidOperationException($"\"{job.Subject}\" isn't a run id.");
}
