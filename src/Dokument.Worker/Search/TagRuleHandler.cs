using Dokument.Domain.Jobs;
using Dokument.Infrastructure.Tags;
using Dokument.Worker.Jobs;

namespace Dokument.Worker.Search;

/// <summary>
/// Runs one keyword rule over every blob's stored text after an admin changed it (docs/design.md §13.4). One BM25 query
/// and one insert, so it's quick even with many files, and running it twice gives the same tags.
/// </summary>
internal sealed class TagRuleHandler(TagRules rules) : IJobHandler
{
    public Task RunAsync(Job job, CancellationToken ct) =>
        Guid.TryParse(job.Subject, out var id)
            ? rules.ApplyRuleAsync(id, ct)
            : throw new InvalidOperationException($"\"{job.Subject}\" isn't a rule id.");
}
