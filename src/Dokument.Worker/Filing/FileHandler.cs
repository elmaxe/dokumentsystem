using Dokument.Domain.Jobs;
using Dokument.Infrastructure.Filing;
using Dokument.Worker.Jobs;

namespace Dokument.Worker.Filing;

/// <summary>Runs its owner's filing rules on one file, after it got a tag or new bytes (docs/design.md §13.5).</summary>
internal sealed class FileHandler(Filer filer) : IJobHandler
{
    public Task RunAsync(Job job, CancellationToken ct) =>
        Guid.TryParse(job.Subject, out var id)
            ? filer.FileAsync(id, ct: ct)
            : throw new InvalidOperationException($"\"{job.Subject}\" isn't a node id.");
}

/// <summary>Files everything that has a filing rule's tag now, when the person asked for it with the rule (docs/design.md §13.5).</summary>
internal sealed class FilingRuleHandler(FilingService filing) : IJobHandler
{
    public Task RunAsync(Job job, CancellationToken ct) =>
        Guid.TryParse(job.Subject, out var id)
            ? filing.FileExistingAsync(id, ct)
            : throw new InvalidOperationException($"\"{job.Subject}\" isn't a rule id.");
}
