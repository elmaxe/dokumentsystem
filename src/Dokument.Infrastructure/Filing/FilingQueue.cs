using Dokument.Domain.Files;
using Dokument.Domain.Jobs;
using Dokument.Infrastructure.Jobs;
using Dokument.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dokument.Infrastructure.Filing;

/// <summary>
/// Queues a <see cref="JobType.File"/> job for files that may have just got a tag or new bytes (docs/design.md §13.6),
/// so their owner's filing rules run on them. Only files whose owner has a filing rule turned on, and that no rule has
/// filed yet, are queued; the job decides whether a rule matches.
/// </summary>
public sealed class FilingQueue(DokumentDbContext db, JobQueue jobs, TimeProvider time)
{
    /// <summary>Queues these files, given with their owners, as the files may not be saved yet. Adds to the unit of work; the caller saves.</summary>
    public async Task RequestAsync(
        IReadOnlyCollection<(Guid OwnerId, Guid NodeId)> files,
        JobPriority priority,
        CancellationToken ct = default
    )
    {
        if (files.Count == 0)
        {
            return;
        }

        var owners = files.Select(f => f.OwnerId).Distinct().ToList();
        var filing = await db
            .FilingRules.Where(r => r.Enabled && owners.Contains(r.OwnerId))
            .Select(r => r.OwnerId)
            .Distinct()
            .ToListAsync(ct);
        if (filing.Count == 0)
        {
            return;
        }

        var candidates = files
            .Where(f => filing.Contains(f.OwnerId))
            .Select(f => f.NodeId)
            .ToList();
        var filed = await db
            .NodeFilings.Where(f => candidates.Contains(f.NodeId))
            .Select(f => f.NodeId)
            .ToListAsync(ct);
        foreach (var id in candidates.Except(filed))
        {
            // A job running now may have read the file's tags before this change.
            await jobs.EnqueueAsync(JobType.File, id.ToString(), priority, ct, evenIfRunning: true);
        }
    }

    /// <summary>Queues every live file whose current bytes are one of these, at once. Returns how many were queued.</summary>
    public Task<int> RequestForBlobsAsync(
        IReadOnlyCollection<ContentHash> hashes,
        JobPriority priority,
        CancellationToken ct = default
    )
    {
        if (hashes.Count == 0)
        {
            return Task.FromResult(0);
        }

        var bytes = hashes.Select(h => h.ToArray()).ToArray();
        var now = time.GetUtcNow();
        return db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO jobs (id, type, subject, status, priority, attempts, run_after, created_at)
            SELECT gen_random_uuid(), {JobType.File.Name}, n.id::text, 'queued', {(int)priority}, 0, {now}, {now}
            FROM nodes n
            JOIN file_versions v ON v.id = n.current_version_id
            WHERE v.blob_hash = ANY({bytes}) AND n.trashed_at IS NULL
              AND EXISTS (SELECT 1 FROM filing_rules r WHERE r.owner_id = n.owner_id AND r.enabled)
              AND NOT EXISTS (SELECT 1 FROM node_filings f WHERE f.node_id = n.id)
              AND NOT EXISTS (SELECT 1 FROM jobs j WHERE j.type = {JobType.File.Name} AND j.subject = n.id::text
                              AND j.status = 'queued')
            """,
            ct
        );
    }
}
