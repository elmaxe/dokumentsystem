using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Dokument.Domain.Audit;
using Dokument.Domain.Files;
using Dokument.Domain.Previews;
using Dokument.Infrastructure.Live;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Previews;
using Dokument.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dokument.Infrastructure.Files;

/// <summary>
/// Permanent deletion (docs/design.md §7, §8.2, §12): whole trashed trees, versions past retention, and the blobs that
/// nothing uses any more. Rows go first; a blob's file is deleted only once its reference count is zero, under a lock
/// that uploads of the same bytes take too. Runs in the server role, which owns the originals volume (§10.4).
/// </summary>
public sealed partial class PurgeService(
    DokumentDbContext db,
    BlobStore blobs,
    DerivedStore derived,
    AuditLog audit,
    LiveChanges live,
    SettingsStore settings,
    TimeProvider time,
    ILogger<PurgeService> logger
)
{
    private const int BatchSize = 200;

    /// <summary>
    /// Takes the blob's lock until the transaction ends. Uploads hold it while they store and record the bytes, and the
    /// purge holds it while it deletes them, so a blob being uploaded again is never deleted from under the upload.
    /// </summary>
    internal static Task LockBlobAsync(
        DokumentDbContext db,
        ContentHash hash,
        CancellationToken ct
    ) =>
        db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock({BinaryPrimitives.ReadInt64BigEndian(hash.Bytes)})",
            ct
        );

    /// <summary>
    /// Deletes a node, everything below it and all their versions, and releases their blobs and quota. Runs in the
    /// caller's transaction. The blob files stay until <see cref="DeleteUnusedBlobsAsync"/>. Returns how many nodes went.
    /// </summary>
    internal async Task<int> DeleteTreeAsync(Node root, CancellationToken ct)
    {
        var released = await db
            .Database.SqlQuery<ReleasedBlob>(
                $"""
                SELECT v.blob_hash AS "BlobHash", n.owner_id AS "OwnerId"
                FROM file_versions v JOIN nodes n ON n.id = v.node_id
                WHERE n.path <@ {root.Path}::ltree
                """
            )
            .ToListAsync(ct);

        // Parents and current versions point back into the tree, so unhook them before the delete.
        await db.Database.ExecuteSqlAsync(
            $"UPDATE nodes SET parent_id = NULL, current_version_id = NULL WHERE path <@ {root.Path}::ltree",
            ct
        );
        var deleted = await db.Database.ExecuteSqlAsync(
            $"DELETE FROM nodes WHERE path <@ {root.Path}::ltree",
            ct
        );
        await ReleaseAsync(released, ct);
        return deleted;
    }

    /// <summary>The whole purge: expired trash, then versions past retention, then unused blobs. Each step works in batches.</summary>
    public async Task<PurgeResult> RunAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var nodes = await PurgeTrashAsync(
            now - (await settings.GetAsync(SettingKeys.Trash, ct)).KeepFor,
            ct
        );
        var versions = await PurgeVersionsAsync(
            (await settings.GetAsync(SettingKeys.Versions, ct)).Retention,
            now,
            ct
        );
        var blobsDeleted = await DeleteUnusedBlobsAsync(ct);
        if (nodes + versions + blobsDeleted > 0)
        {
            LogPurged(nodes, versions, blobsDeleted);
        }

        return new PurgeResult(nodes, versions, blobsDeleted);
    }

    /// <summary>Deletes everything trashed before <paramref name="cutoff"/>, one trash group per transaction.</summary>
    internal async Task<int> PurgeTrashAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        var total = 0;
        while (true)
        {
            var roots = await db
                .Nodes.AsNoTracking()
                .Where(n => n.TrashedRootId == n.Id && n.TrashedAt <= cutoff)
                .OrderBy(n => n.TrashedAt)
                .Take(BatchSize)
                .ToListAsync(ct);
            if (roots.Count == 0)
            {
                return total;
            }

            foreach (var root in roots)
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var deleted = await DeleteTreeAsync(root, ct);
                if (deleted > 0)
                {
                    audit.AddSystem(
                        AuditActions.NodePurge,
                        root.Id,
                        details: new JsonObject
                        {
                            ["name"] = root.Name.Value,
                            ["nodes"] = deleted,
                            ["trashedAt"] = root.TrashedAt,
                        }
                    );
                    await db.SaveChangesAsync(ct);
                    await live.NotifyAsync(root.OwnerId, LiveTopics.Trash | LiveTopics.Storage, ct);
                }

                await tx.CommitAsync(ct);
                total += deleted;
            }
        }
    }

    /// <summary>
    /// Deletes non-current versions that <paramref name="retention"/> no longer keeps: superseded for longer than its age,
    /// or beyond its count. The same rule as <see cref="VersionRetention.PurgeDates"/>, in SQL.
    /// </summary>
    internal async Task<int> PurgeVersionsAsync(
        VersionRetention retention,
        DateTimeOffset now,
        CancellationToken ct
    )
    {
        var cutoff = now - retention.MaxAge;
        var total = 0;
        while (true)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var due = await db
                .Database.SqlQuery<DueVersion>(
                    $"""
                    SELECT x.id AS "Id", x.node_id AS "NodeId", x.version_no AS "Number", x.blob_hash AS "BlobHash",
                           x.owner_id AS "OwnerId"
                    FROM (
                        SELECT v.id, v.node_id, v.version_no, v.blob_hash, v.keep_forever, n.owner_id, n.current_version_id,
                               COALESCE(lead(v.uploaded_at) OVER (PARTITION BY v.node_id ORDER BY v.version_no), v.uploaded_at)
                                   AS superseded_at,
                               row_number() OVER (PARTITION BY v.node_id ORDER BY v.version_no DESC) AS rank
                        FROM file_versions v JOIN nodes n ON n.id = v.node_id
                        WHERE v.node_id IN (SELECT node_id FROM file_versions GROUP BY node_id HAVING count(*) > 1)
                    ) x
                    WHERE x.id IS DISTINCT FROM x.current_version_id AND NOT x.keep_forever
                      AND (x.superseded_at <= {cutoff} OR x.rank > {retention.MaxVersions})
                    ORDER BY x.superseded_at
                    LIMIT {BatchSize}
                    """
                )
                .ToListAsync(ct);
            if (due.Count == 0)
            {
                return total;
            }

            // Checked again on delete: someone may have chosen Keep forever since the query.
            var ids = due.Select(v => v.Id).ToArray();
            var deleted = (
                await db
                    .Database.SqlQuery<Guid>(
                        $"""
                        DELETE FROM file_versions v USING nodes n
                        WHERE v.id = ANY({ids}) AND n.id = v.node_id AND n.current_version_id IS DISTINCT FROM v.id
                          AND NOT v.keep_forever
                        RETURNING v.id AS "Value"
                        """
                    )
                    .ToListAsync(ct)
            ).ToHashSet();
            due.RemoveAll(v => !deleted.Contains(v.Id));
            await ReleaseAsync(
                due.Select(v => new ReleasedBlob { BlobHash = v.BlobHash, OwnerId = v.OwnerId })
                    .ToList(),
                ct
            );
            foreach (var version in due)
            {
                audit.AddSystem(
                    AuditActions.VersionPurge,
                    version.NodeId,
                    version.Id,
                    new JsonObject { ["versionNo"] = version.Number }
                );
            }

            await db.SaveChangesAsync(ct);
            await live.NotifyAsync(
                due.Select(v => v.OwnerId),
                LiveTopics.Files | LiveTopics.Storage,
                ct
            );
            await tx.CommitAsync(ct);
            total += due.Count;
        }
    }

    /// <summary>
    /// Deletes blobs whose reference count reached zero: the row and the owner's file, and the renditions too once nobody
    /// has those bytes any more.
    /// </summary>
    internal async Task<int> DeleteUnusedBlobsAsync(CancellationToken ct)
    {
        var total = 0;
        var failed = new HashSet<(Guid, ContentHash)>();
        while (true)
        {
            var unused = (
                await db
                    .Blobs.AsNoTracking()
                    .Where(b => b.RefCount <= 0)
                    .OrderBy(b => b.CreatedAt)
                    .Select(b => new { b.OwnerId, b.Hash })
                    .Take(BatchSize + failed.Count)
                    .ToListAsync(ct)
            )
                .Select(b => (b.OwnerId, b.Hash))
                .Where(b => !failed.Contains(b))
                .ToList();
            if (unused.Count == 0)
            {
                return total;
            }

            foreach (var (ownerId, hash) in unused)
            {
                try
                {
                    if (await DeleteBlobAsync(ownerId, hash, ct))
                    {
                        total++;
                    }
                }
                catch (IOException e)
                {
                    // Left for the next run; the row stays, so nothing is lost track of.
                    LogBlobDeleteFailed(e, hash.Hex);
                    failed.Add((ownerId, hash));
                }
            }
        }
    }

    private async Task<bool> DeleteBlobAsync(Guid ownerId, ContentHash hash, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockBlobAsync(db, hash, ct);
        var keys = await db
            .Database.SqlQuery<string>(
                $"""DELETE FROM blobs WHERE owner_id = {ownerId} AND hash = {hash.ToArray()} AND ref_count <= 0 RETURNING storage_key AS "Value" """
            )
            .ToListAsync(ct);
        if (keys.Count == 0)
        {
            return false;
        }

        // Renditions belong to the bytes, not to an owner, so they go with the last copy.
        List<DerivedKey> renditions = [];
        if (!await db.Blobs.AnyAsync(b => b.Hash == hash, ct))
        {
            renditions = await db
                .Renditions.Where(r => r.BlobHash == hash)
                .Select(r => r.StorageKey)
                .ToListAsync(ct);
            await db.Renditions.Where(r => r.BlobHash == hash).ExecuteDeleteAsync(ct);
            await db.BlobPreviews.Where(p => p.BlobHash == hash).ExecuteDeleteAsync(ct);
        }

        // The files go while the lock is held: an upload of the same bytes waits, then finds no file and stores its own.
        File.Delete(blobs.PathOf(StorageKey.From(keys[0])));
        foreach (var rendition in renditions)
        {
            derived.Delete(rendition);
        }

        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>
    /// Lowers the reference count of each released blob, and the owner's used storage for each blob they no longer have
    /// any version of (each owner has their own copy, §7, §17). Call after the versions are deleted.
    /// </summary>
    private async Task ReleaseAsync(List<ReleasedBlob> released, CancellationToken ct)
    {
        if (released.Count == 0)
        {
            return;
        }

        var hashes = released.Select(r => r.BlobHash).ToArray();
        var owners = released.Select(r => r.OwnerId).ToArray();
        await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE blobs b SET ref_count = b.ref_count - x.n
            FROM (
                SELECT owner_id, hash, count(*)::int AS n
                FROM unnest({owners}, {hashes}) AS t(owner_id, hash) GROUP BY owner_id, hash
            ) x
            WHERE b.owner_id = x.owner_id AND b.hash = x.hash
            """,
            ct
        );
        await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE users u SET used_bytes = GREATEST(u.used_bytes - x.freed, 0)
            FROM (
                SELECT o.owner_id, sum(b.size) AS freed
                FROM (SELECT DISTINCT owner_id, hash FROM unnest({owners}, {hashes}) AS t(owner_id, hash)) o
                JOIN blobs b ON b.owner_id = o.owner_id AND b.hash = o.hash
                WHERE NOT EXISTS (
                    SELECT 1 FROM file_versions v JOIN nodes n ON n.id = v.node_id
                    WHERE v.blob_hash = o.hash AND n.owner_id = o.owner_id)
                GROUP BY o.owner_id
            ) x
            WHERE u.id = x.owner_id
            """,
            ct
        );
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Purged {Nodes} trashed items, {Versions} old versions and {Blobs} unused files"
    )]
    private partial void LogPurged(int nodes, int versions, int blobs);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Couldn't delete blob {Hash}; trying again on the next run"
    )]
    private partial void LogBlobDeleteFailed(Exception e, string hash);

    private sealed class ReleasedBlob
    {
        public required byte[] BlobHash { get; init; }
        public required Guid OwnerId { get; init; }
    }

    private sealed class DueVersion
    {
        public required Guid Id { get; init; }
        public required Guid NodeId { get; init; }
        public required int Number { get; init; }
        public required byte[] BlobHash { get; init; }
        public required Guid OwnerId { get; init; }
    }
}

public sealed record PurgeResult(int Nodes, int Versions, int Blobs);

/// <summary>Asks the purge to run now rather than at the next hour, e.g. after "Delete forever" so the space frees in seconds.</summary>
public sealed class PurgeSignal
{
    private readonly SemaphoreSlim pending = new(0, 1);

    public void Request()
    {
        try
        {
            pending.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already requested.
        }
    }

    /// <summary>True if a run was requested before <paramref name="timeout"/> passed.</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) =>
        pending.WaitAsync(timeout, ct);
}
