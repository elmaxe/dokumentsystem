using System.Text.Json.Nodes;
using Dokument.Application.Files;
using Dokument.Domain.Audit;
using Dokument.Domain.Files;
using Dokument.Domain.Identity;
using Dokument.Domain.Jobs;
using Dokument.Infrastructure.Live;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Previews;
using Dokument.Infrastructure.Search;
using Dokument.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dokument.Infrastructure.Files;

/// <summary>Folders, files and versions (docs/design.md §6, §8). Every operation checks access through <see cref="NodeAccess"/>.</summary>
public sealed class DriveService(
    DokumentDbContext db,
    BlobStore blobs,
    AuditLog audit,
    PreviewService previews,
    ContentService content,
    SettingsStore settings,
    RecentFiles recent,
    LiveChanges live,
    TimeProvider time
)
{
    private IQueryable<Node> Live(Actor actor) =>
        db.Nodes.VisibleTo(actor).Where(n => n.TrashedAt == null);

    public async Task<Node> GetAsync(Actor actor, Guid id, CancellationToken ct = default) =>
        await Live(actor).Include(n => n.CurrentVersion).SingleOrDefaultAsync(n => n.Id == id, ct)
        ?? throw DriveException.NotFound();

    /// <summary>The folder with this id, or null for the root.</summary>
    public async Task<Node?> GetFolderAsync(Actor actor, Guid? id, CancellationToken ct = default)
    {
        if (id is not { } folderId)
        {
            return null;
        }

        var node = await GetAsync(actor, folderId, ct);
        return node.IsFolder
            ? node
            : throw new DriveException(DriveError.InvalidRequest, "That is a file, not a folder.");
    }

    public async Task<Node> GetFileAsync(Actor actor, Guid id, CancellationToken ct = default)
    {
        var node = await GetAsync(actor, id, ct);
        return node.IsFolder
            ? throw new DriveException(DriveError.InvalidRequest, "That is a folder, not a file.")
            : node;
    }

    public async Task<List<Node>> ListChildrenAsync(
        Actor actor,
        Guid? folderId,
        CancellationToken ct = default
    )
    {
        await GetFolderAsync(actor, folderId, ct);
        return await Live(actor)
            .Include(n => n.CurrentVersion)
            .Where(n => n.ParentId == folderId)
            .ToListAsync(ct);
    }

    /// <summary>The folders above each node, root first, keyed by node id. One query however many nodes.</summary>
    public async Task<Dictionary<Guid, List<Node>>> AncestorsAsync(
        IReadOnlyCollection<Node> nodes,
        CancellationToken ct = default
    )
    {
        var chains = nodes.ToDictionary(
            n => n.Id,
            n => n.Path.Split('.')[..^1].Select(label => Guid.ParseExact(label, "N")).ToList()
        );
        var ids = chains.Values.SelectMany(c => c).Distinct().ToList();
        var byId = await db
            .Nodes.AsNoTracking()
            .Where(n => ids.Contains(n.Id))
            .ToDictionaryAsync(n => n.Id, ct);
        return chains.ToDictionary(c => c.Key, c => c.Value.Select(id => byId[id]).ToList());
    }

    /// <summary>
    /// What each folder holds, keyed by folder id (§16.4): the current versions of every file below it and how many
    /// files and folders that is, in one query however many folders. A live folder counts its live contents; a trashed
    /// one counts what went to the trash with it.
    /// </summary>
    public async Task<Dictionary<Guid, FolderContents>> FolderContentsAsync(
        IReadOnlyCollection<Node> folders,
        CancellationToken ct = default
    )
    {
        var ids = folders.Where(f => f.IsFolder).Select(f => f.Id).ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await db
            .Database.SqlQuery<FolderContentsRow>(
                $"""
                SELECT f.id AS "FolderId",
                       COALESCE(sum(v.size), 0)::bigint AS "Bytes",
                       count(*) FILTER (WHERE n.kind = 'file')::int AS "Files",
                       count(*) FILTER (WHERE n.kind = 'folder')::int AS "Folders"
                FROM nodes f
                JOIN nodes n ON n.path <@ f.path AND n.id <> f.id
                    AND n.trashed_root_id IS NOT DISTINCT FROM f.trashed_root_id
                LEFT JOIN file_versions v ON v.id = n.current_version_id
                WHERE f.id = ANY({ids})
                GROUP BY f.id
                """
            )
            .ToListAsync(ct);
        var found = rows.ToDictionary(
            r => r.FolderId,
            r => new FolderContents(r.Bytes, r.Files, r.Folders)
        );
        return ids.ToDictionary(id => id, id => found.GetValueOrDefault(id, FolderContents.Empty));
    }

    private sealed record FolderContentsRow(Guid FolderId, long Bytes, int Files, int Folders);

    public async Task<Node> CreateFolderAsync(
        Actor actor,
        Guid? parentId,
        string name,
        CancellationToken ct = default
    )
    {
        var folderName = ParseName(name);
        var parent = await GetFolderAsync(actor, parentId, ct);
        var ownerId = parent?.OwnerId ?? actor.UserId;
        await EnsureNameFreeAsync(ownerId, parentId, folderName, except: null, ct);

        var now = time.GetUtcNow();
        var folder = new Node
        {
            OwnerId = ownerId,
            Kind = NodeKind.Folder,
            Name = folderName,
            CreatedAt = now,
            ModifiedAt = now,
        };
        folder.PlaceUnder(parent);
        db.Nodes.Add(folder);
        audit.Add(actor, AuditActions.FolderCreate, folder.Id);
        await SaveAsync(ownerId, parentId, folderName, ct);
        await live.NotifyAsync(ownerId, LiveTopics.Files, ct);
        return folder;
    }

    /// <summary>
    /// The folders of a folder upload (§8.1), created in one go before any file is sent. Each path is relative to
    /// <paramref name="parentId"/>, its names separated by "/"; the folders above a path are made too. A top folder whose
    /// name is taken follows <paramref name="onConflict"/>; below a merged folder, folders with the same names are reused.
    /// Nothing is created unless <paramref name="bytes"/>, the size of all the files, fits in the owner's quota.
    /// </summary>
    public async Task<List<TreeFolder>> CreateFolderTreeAsync(
        Actor actor,
        Guid? parentId,
        IReadOnlyCollection<string> paths,
        FolderConflictPolicy onConflict,
        long bytes,
        CancellationToken ct = default
    )
    {
        var requested = paths.Select(p => (Path: p, Names: ParsePath(p))).ToList();
        var parent = await GetFolderAsync(actor, parentId, ct);
        var ownerId = parent?.OwnerId ?? actor.UserId;
        await EnsureQuotaFitsAsync(ownerId, bytes, ct);

        // Every folder named, and the folders above them, shallowest first, keyed by their case-insensitive path.
        var all = requested
            .SelectMany(r => Enumerable.Range(1, r.Names.Count).Select(n => r.Names[..n]))
            .DistinctBy(PathKey)
            .OrderBy(names => names.Count)
            .ToList();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = time.GetUtcNow();
        var made = new Dictionary<string, (Node Folder, bool Created)>();
        foreach (var names in all)
        {
            var (under, underCreated) =
                names.Count == 1 ? (parent, false) : made[PathKey(names[..^1])];
            var name = names[^1];
            Node? folder = null;

            // A folder made just now has nothing in it to clash with.
            if (
                !underCreated && await FindByNameAsync(ownerId, under?.Id, name, ct) is { } existing
            )
            {
                switch (names.Count == 1 ? onConflict : FolderConflictPolicy.Merge)
                {
                    case FolderConflictPolicy.Fail:
                        throw NameTaken(existing);
                    case FolderConflictPolicy.Merge when existing.IsFolder:
                        folder = existing;
                        break;
                    default:
                        name = await FreeNameAsync(ownerId, under?.Id, name, ct);
                        break;
                }
            }

            if (folder is null)
            {
                folder = new Node
                {
                    OwnerId = ownerId,
                    Kind = NodeKind.Folder,
                    Name = name,
                    CreatedAt = now,
                    ModifiedAt = now,
                };
                folder.PlaceUnder(under);
                db.Nodes.Add(folder);
                audit.Add(actor, AuditActions.FolderCreate, folder.Id);
                made[PathKey(names)] = (folder, true);
            }
            else
            {
                made[PathKey(names)] = (folder, false);
            }
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e)
            when (e.InnerException
                    is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
            )
        {
            db.ChangeTracker.Clear();
            throw new DriveException(
                DriveError.NameTaken,
                "Something with one of these names was added at the same time. Try again."
            );
        }

        if (made.Values.Any(m => m.Created))
        {
            await live.NotifyAsync(ownerId, LiveTopics.Files, ct);
        }

        await tx.CommitAsync(ct);

        // What merged folders already hold, so the client can ask about name clashes before uploading.
        var reused = made.Values.Where(m => !m.Created).Select(m => (Guid?)m.Folder.Id).ToList();
        var held = (
            await db
                .Nodes.AsNoTracking()
                .Where(n =>
                    n.OwnerId == ownerId && reused.Contains(n.ParentId) && n.TrashedAt == null
                )
                .Select(n => new { n.ParentId, n.Name })
                .ToListAsync(ct)
        ).ToLookup(n => n.ParentId!.Value, n => n.Name.Value);

        return requested
            .Select(r =>
            {
                var (folder, created) = made[PathKey(r.Names)];
                return new TreeFolder(r.Path, folder, created, [.. held[folder.Id]]);
            })
            .ToList();
    }

    private static List<NodeName> ParsePath(string path) =>
        string.IsNullOrEmpty(path)
            ? throw new DriveException(DriveError.InvalidRequest, "A folder path is empty.")
            : path.Split('/').Select(ParseName).ToList();

    private static string PathKey(List<NodeName> names) =>
        string.Join('/', names.Select(n => n.Key));

    public async Task<Node> RenameAsync(
        Actor actor,
        Guid id,
        string name,
        CancellationToken ct = default
    )
    {
        var newName = ParseName(name);
        var node = await GetAsync(actor, id, ct);
        if (node.Name == newName)
        {
            return node;
        }

        await EnsureNameFreeAsync(node.OwnerId, node.ParentId, newName, except: node.Id, ct);
        var oldName = node.Name;
        node.Name = newName;
        node.ModifiedAt = time.GetUtcNow();
        audit.Add(
            actor,
            AuditActions.NodeRename,
            node.Id,
            details: new JsonObject { ["from"] = oldName.Value, ["to"] = newName.Value }
        );
        await SaveAsync(node.OwnerId, node.ParentId, newName, ct);
        await live.NotifyAsync(node.OwnerId, LiveTopics.Files, ct);
        return node;
    }

    public async Task<Node> MoveAsync(
        Actor actor,
        Guid id,
        Guid? folderId,
        CancellationToken ct = default
    )
    {
        var node = await GetAsync(actor, id, ct);
        var target = await GetFolderAsync(actor, folderId, ct);
        if (node.ParentId == folderId)
        {
            return node;
        }

        if (target is not null && node.Contains(target))
        {
            throw new DriveException(
                DriveError.InvalidRequest,
                "A folder can't be moved into itself."
            );
        }

        if (target is not null && target.OwnerId != node.OwnerId)
        {
            throw new DriveException(
                DriveError.InvalidRequest,
                "Items can only be moved within the same owner's files."
            );
        }

        await EnsureNameFreeAsync(node.OwnerId, folderId, node.Name, except: node.Id, ct);
        var fromId = node.ParentId;
        var oldPath = node.Path;
        node.PlaceUnder(target);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (node.IsFolder)
        {
            // Descendants keep their path below the folder and take its new prefix.
            await db.Database.ExecuteSqlAsync(
                $"UPDATE nodes SET path = {node.Path}::ltree || subpath(path, nlevel({oldPath}::ltree)) WHERE path <@ {oldPath}::ltree AND id <> {node.Id}",
                ct
            );
        }

        audit.Add(
            actor,
            AuditActions.NodeMove,
            node.Id,
            details: new JsonObject { ["from"] = fromId?.ToString(), ["to"] = folderId?.ToString() }
        );
        await SaveAsync(node.OwnerId, folderId, node.Name, ct);
        await live.NotifyAsync(node.OwnerId, LiveTopics.Files, ct);
        await tx.CommitAsync(ct);
        return node;
    }

    public async Task<List<FileVersion>> ListVersionsAsync(
        Actor actor,
        Guid id,
        CancellationToken ct = default
    )
    {
        var node = await GetFileAsync(actor, id, ct);
        return await db
            .FileVersions.AsNoTracking()
            .Where(v => v.NodeId == node.Id)
            .OrderByDescending(v => v.Number)
            .ToListAsync(ct);
    }

    /// <summary>Makes an old version current by adding a new version with its bytes (§8.2). History is never rewritten.</summary>
    public async Task<FileVersion> RestoreVersionAsync(
        Actor actor,
        Guid id,
        Guid versionId,
        CancellationToken ct = default
    )
    {
        var node = await GetFileAsync(actor, id, ct);
        var old =
            await db
                .FileVersions.AsNoTracking()
                .SingleOrDefaultAsync(v => v.NodeId == node.Id && v.Id == versionId, ct)
            ?? throw DriveException.NotFound("version");
        if (old.Id == node.CurrentVersionId)
        {
            return old;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PurgeService.LockBlobAsync(db, old.BlobHash, ct);
        if (
            !await db.Blobs.AnyAsync(
                b => b.OwnerId == node.OwnerId && b.Hash == old.BlobHash && b.RefCount > 0,
                ct
            )
        )
        {
            // The purge job removed this version a moment ago.
            throw DriveException.NotFound("version");
        }

        var version = await AddVersionAsync(
            node,
            old.BlobHash,
            old.Size,
            old.MimeType,
            old.OriginalName,
            actor,
            clientModifiedAt: old.ClientModifiedAt,
            restoredFrom: old.Number,
            ct
        );
        audit.Add(
            actor,
            AuditActions.VersionRestore,
            node.Id,
            version.Id,
            new JsonObject { ["restoredFrom"] = old.Number }
        );
        await recent.RecordAsync(actor.UserId, node.Id, RecentAction.Restored, ct);
        await db.SaveChangesAsync(ct);
        await live.NotifyAsync([node.OwnerId, actor.UserId], LiveTopics.Files, ct);
        await tx.CommitAsync(ct);
        return version;
    }

    public async Task<FileVersion> SetKeepForeverAsync(
        Actor actor,
        Guid id,
        Guid versionId,
        bool keepForever,
        CancellationToken ct = default
    )
    {
        var node = await GetFileAsync(actor, id, ct);
        var version =
            await db.FileVersions.SingleOrDefaultAsync(
                v => v.NodeId == node.Id && v.Id == versionId,
                ct
            ) ?? throw DriveException.NotFound("version");
        if (version.KeepForever != keepForever)
        {
            version.KeepForever = keepForever;
            audit.Add(
                actor,
                AuditActions.VersionKeepForever,
                node.Id,
                version.Id,
                new JsonObject { ["keepForever"] = keepForever }
            );
            await db.SaveChangesAsync(ct);
            await live.NotifyAsync(node.OwnerId, LiveTopics.Files, ct);
        }

        return version;
    }

    /// <summary>
    /// Finds the bytes of a file's current (or given) version. Downloads of the original are audited; previews are
    /// not (§18), so callers pass <paramref name="audited"/> false for preview-style reads.
    /// </summary>
    public async Task<FileContent> OpenContentAsync(
        Actor actor,
        Guid id,
        Guid? versionId,
        bool audited,
        CancellationToken ct = default
    )
    {
        var node = await GetFileAsync(actor, id, ct);
        var wanted = versionId ?? node.CurrentVersionId;
        var version =
            await db
                .FileVersions.AsNoTracking()
                .SingleOrDefaultAsync(v => v.NodeId == node.Id && v.Id == wanted, ct)
            ?? throw DriveException.NotFound("version");
        var blob = await db
            .Blobs.AsNoTracking()
            .SingleAsync(b => b.OwnerId == node.OwnerId && b.Hash == version.BlobHash, ct);
        if (audited)
        {
            audit.Add(actor, AuditActions.FileDownload, node.Id, version.Id);
            await recent.RecordAsync(actor.UserId, node.Id, RecentAction.Downloaded, ct);
            await db.SaveChangesAsync(ct);
            await live.NotifyAsync(actor.UserId, LiveTopics.Recent, ct);
        }

        return new FileContent(node, version, blobs.PathOf(blob.StorageKey));
    }

    /// <summary>
    /// The pre-upload duplicate check (§8.3): for each hash, the files the user can already see with those bytes. Only
    /// the user's own accessible files are ever reported, so nobody learns what someone else has.
    /// </summary>
    public async Task<Dictionary<ContentHash, List<Node>>> FindByContentAsync(
        Actor actor,
        IReadOnlyCollection<ContentHash> hashes,
        CancellationToken ct = default
    )
    {
        var keys = hashes.Distinct().ToList();
        var matches = await db
            .Nodes.VisibleTo(actor)
            .Include(n => n.CurrentVersion)
            .Where(n => n.CurrentVersion != null && keys.Contains(n.CurrentVersion.BlobHash))
            .OrderBy(n => n.TrashedAt != null)
            .ThenBy(n => n.CreatedAt)
            .ToListAsync(ct);
        return keys.ToDictionary(
            h => h,
            h => matches.Where(n => n.CurrentVersion!.BlobHash == h).ToList()
        );
    }

    /// <summary>
    /// "Upload anyway" for a file the user already has (§8.3): a new file pointing at the same blob, with no bytes
    /// sent and no extra quota. Only allowed when the user can already see a file with those bytes.
    /// </summary>
    public async Task<AddedFile> CopyFromContentAsync(
        Actor actor,
        Guid? folderId,
        string name,
        ContentHash hash,
        DateTimeOffset? clientModifiedAt,
        NameConflictPolicy onConflict,
        CancellationToken ct = default
    )
    {
        var fileName = ParseName(name);
        var source =
            await db
                .FileVersions.AsNoTracking()
                .Where(v =>
                    v.BlobHash == hash && db.Nodes.VisibleTo(actor).Any(n => n.Id == v.NodeId)
                )
                .FirstOrDefaultAsync(ct)
            ?? throw DriveException.NotFound("file with that content");
        return await AddFileAsync(
            actor,
            new NewFile(
                folderId,
                TargetNodeId: null,
                fileName,
                hash,
                source.Size,
                MimeTypes.For(fileName.Value, source.MimeType),
                clientModifiedAt,
                onConflict
            ),
            reservedBytes: 0,
            AuditActions.FileCopy,
            storeBytes: null,
            ct
        );
    }

    /// <summary>
    /// Records bytes that are already in the blob store as a new file, or a new version of an existing one, and settles
    /// the quota: the reservation is released and the size counts once per blob per owner (§17).
    /// <paramref name="storeBytes"/> moves the bytes into the owner's folder of the blob store; it runs under the blob's
    /// lock, so the purge job can't delete a blob file that is being reused (§12). Without it, the bytes must already be
    /// stored: the owner's own copy is reused, or another owner's is copied in (a copy of a file shared with them).
    /// </summary>
    internal async Task<AddedFile> AddFileAsync(
        Actor actor,
        NewFile file,
        long reservedBytes,
        string auditAction,
        Action<Guid>? storeBytes,
        CancellationToken ct
    )
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = time.GetUtcNow();
        await PurgeService.LockBlobAsync(db, file.Hash, ct);

        Node? existing;
        Guid ownerId;
        var name = file.Name;
        if (file.TargetNodeId is { } targetId)
        {
            existing = await GetFileAsync(actor, targetId, ct);
            ownerId = existing.OwnerId;
        }
        else
        {
            var parent = await GetFolderAsync(actor, file.FolderId, ct);
            ownerId = parent?.OwnerId ?? actor.UserId;
            existing = await FindByNameAsync(ownerId, file.FolderId, name, ct);
            if (existing is not null)
            {
                switch (file.OnConflict)
                {
                    case NameConflictPolicy.Fail:
                        throw NameTaken(existing);
                    case NameConflictPolicy.NewVersion when !existing.IsFolder:
                        break;
                    default:
                        name = await FreeNameAsync(ownerId, file.FolderId, name, ct);
                        existing = null;
                        break;
                }
            }
        }

        await StoreAsync(ownerId, file.Hash, storeBytes, ct);
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO blobs (owner_id, hash, size, storage_key, ref_count, created_at) VALUES ({ownerId}, {file.Hash.ToArray()}, {file.Size}, {StorageKey.For(ownerId, file.Hash).Value}, 0, {now}) ON CONFLICT (owner_id, hash) DO NOTHING",
            ct
        );

        // Same bytes as the current version: nothing new to keep (§6).
        if (existing?.CurrentVersion?.BlobHash == file.Hash)
        {
            await SettleQuotaAsync(ownerId, reservedBytes, addedBytes: 0, ct);
            await live.NotifyAsync(ownerId, LiveTopics.Storage, ct);
            await tx.CommitAsync(ct);
            return new AddedFile(
                existing,
                existing.CurrentVersion,
                Created: false,
                NewVersion: false
            );
        }

        var alreadyOwned = await db.FileVersions.AnyAsync(
            v =>
                v.BlobHash == file.Hash
                && db.Nodes.Any(n => n.Id == v.NodeId && n.OwnerId == ownerId),
            ct
        );
        var node = existing;
        if (node is null)
        {
            node = new Node
            {
                OwnerId = ownerId,
                Kind = NodeKind.File,
                Name = name,
                CreatedAt = now,
                ModifiedAt = now,
            };
            node.PlaceUnder(await GetFolderAsync(actor, file.FolderId, ct));
            db.Nodes.Add(node);
            await SaveAsync(ownerId, file.FolderId, name, ct);
        }

        var version = await AddVersionAsync(
            node,
            file.Hash,
            file.Size,
            file.MimeType,
            file.Name,
            actor,
            file.ClientModifiedAt,
            restoredFrom: null,
            ct
        );
        await SettleQuotaAsync(ownerId, reservedBytes, alreadyOwned ? 0 : file.Size, ct);
        audit.Add(
            actor,
            existing is null ? auditAction : AuditActions.VersionUpload,
            node.Id,
            version.Id,
            new JsonObject { ["size"] = file.Size, ["sha256"] = file.Hash.Hex }
        );
        await db.SaveChangesAsync(ct);
        await recent.RecordAsync(actor.UserId, node.Id, RecentAction.Uploaded, ct);
        await live.NotifyAsync([ownerId, actor.UserId], LiveTopics.Files | LiveTopics.Storage, ct);
        await tx.CommitAsync(ct);
        return new AddedFile(
            node,
            version,
            Created: existing is null,
            NewVersion: existing is not null
        );
    }

    /// <summary>
    /// Makes sure the owner has their own copy of the bytes on disk, under the blob's lock: the upload's, the one they
    /// already have, or one copied from another owner who has it.
    /// </summary>
    private async Task StoreAsync(
        Guid ownerId,
        ContentHash hash,
        Action<Guid>? storeBytes,
        CancellationToken ct
    )
    {
        if (storeBytes is not null)
        {
            storeBytes(ownerId);
            return;
        }

        if (blobs.Exists(StorageKey.For(ownerId, hash)))
        {
            return;
        }

        var source =
            await db
                .Blobs.AsNoTracking()
                .Where(b => b.Hash == hash && b.RefCount > 0)
                .Select(b => b.StorageKey)
                .FirstOrDefaultAsync(ct)
            ?? throw DriveException.NotFound("file with that content");
        blobs.CopyIn(source, ownerId, hash);
    }

    /// <summary>Adds the next version of a file and makes it current. The caller saves.</summary>
    private async Task<FileVersion> AddVersionAsync(
        Node node,
        ContentHash hash,
        long size,
        string mimeType,
        NodeName originalName,
        Actor actor,
        DateTimeOffset? clientModifiedAt,
        int? restoredFrom,
        CancellationToken ct
    )
    {
        var now = time.GetUtcNow();
        var number =
            (
                await db
                    .FileVersions.Where(v => v.NodeId == node.Id)
                    .MaxAsync(v => (int?)v.Number, ct)
                ?? 0
            ) + 1;
        var version = new FileVersion
        {
            NodeId = node.Id,
            Number = number,
            BlobHash = hash,
            Size = size,
            MimeType = mimeType,
            OriginalName = originalName,
            UploadedBy = actor.UserId,
            UploadedAt = now,
            ClientModifiedAt = clientModifiedAt,
            RestoredFrom = restoredFrom,
        };
        db.FileVersions.Add(version);

        // The node row and the version row point at each other, so the version is saved before it becomes current.
        await db.SaveChangesAsync(ct);
        node.CurrentVersionId = version.Id;
        node.CurrentVersion = version;
        node.ModifiedAt = now;
        await db
            .Blobs.Where(b => b.OwnerId == node.OwnerId && b.Hash == hash)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.RefCount, b => b.RefCount + 1), ct);
        await previews.RequestAsync(hash, JobPriority.Interactive, ct);
        await content.RequestAsync(hash, JobPriority.Interactive, ct);
        return version;
    }

    /// <summary>Reserves quota for an upload (§8.1). Fails when the owner's quota can't hold it.</summary>
    internal async Task ReserveQuotaAsync(Guid ownerId, long bytes, CancellationToken ct)
    {
        var defaultQuota = (await settings.GetAsync(SettingKeys.Storage, ct)).DefaultQuota.Bytes;
        var reserved = await db
            .Users.Where(u =>
                u.Id == ownerId
                && (
                    u.QuotaUnlimited
                    || (u.QuotaBytes ?? defaultQuota) == null
                    || u.UsedBytes + u.ReservedBytes + bytes <= (u.QuotaBytes ?? defaultQuota)
                )
            )
            .ExecuteUpdateAsync(
                s => s.SetProperty(u => u.ReservedBytes, u => u.ReservedBytes + bytes),
                ct
            );
        if (reserved == 0)
        {
            throw new DriveException(
                DriveError.QuotaExceeded,
                "There isn't enough storage left for this file."
            );
        }
    }

    /// <summary>Checks, without reserving anything, that <paramref name="bytes"/> more would fit in the owner's quota.</summary>
    internal async Task EnsureQuotaFitsAsync(Guid ownerId, long bytes, CancellationToken ct)
    {
        if (bytes <= 0)
        {
            return;
        }

        var defaultQuota = (await settings.GetAsync(SettingKeys.Storage, ct)).DefaultQuota.Bytes;
        var fits = await db.Users.AnyAsync(
            u =>
                u.Id == ownerId
                && (
                    u.QuotaUnlimited
                    || (u.QuotaBytes ?? defaultQuota) == null
                    || u.UsedBytes + u.ReservedBytes + bytes <= (u.QuotaBytes ?? defaultQuota)
                ),
            ct
        );
        if (!fits)
        {
            throw new DriveException(
                DriveError.QuotaExceeded,
                "There isn't enough storage left for these files."
            );
        }
    }

    internal Task SettleQuotaAsync(
        Guid ownerId,
        long reservedBytes,
        long addedBytes,
        CancellationToken ct
    ) =>
        reservedBytes == 0 && addedBytes == 0
            ? Task.CompletedTask
            : db
                .Users.Where(u => u.Id == ownerId)
                .ExecuteUpdateAsync(
                    s =>
                        s.SetProperty(u => u.ReservedBytes, u => u.ReservedBytes - reservedBytes)
                            .SetProperty(u => u.UsedBytes, u => u.UsedBytes + addedBytes),
                    ct
                );

    internal Task<Node?> FindByNameAsync(
        Guid ownerId,
        Guid? folderId,
        NodeName name,
        CancellationToken ct
    ) =>
        db
            .Nodes.Include(n => n.CurrentVersion)
            .FirstOrDefaultAsync(
                n =>
                    n.OwnerId == ownerId
                    && n.ParentId == folderId
                    && n.NameKey == name.Key
                    && n.TrashedAt == null,
                ct
            );

    internal async Task EnsureNameFreeAsync(
        Guid ownerId,
        Guid? folderId,
        NodeName name,
        Guid? except,
        CancellationToken ct
    )
    {
        var existing = await FindByNameAsync(ownerId, folderId, name, ct);
        if (existing is not null && existing.Id != except)
        {
            throw NameTaken(existing);
        }
    }

    internal static NodeName ParseName(string? name) =>
        NodeName.TryCreate(name, out var parsed, out var error)
            ? parsed
            : throw new DriveException(DriveError.InvalidRequest, error);

    internal async Task<NodeName> FreeNameAsync(
        Guid ownerId,
        Guid? folderId,
        NodeName name,
        CancellationToken ct
    )
    {
        for (var n = 1; ; n++)
        {
            var candidate = name.WithCopyNumber(n);
            if (await FindByNameAsync(ownerId, folderId, candidate, ct) is null)
            {
                return candidate;
            }
        }
    }

    private static DriveException NameTaken(Node existing) =>
        new(
            DriveError.NameTaken,
            $"There's already {(existing.IsFolder ? "a folder" : "a file")} called \"{existing.Name}\" here.",
            existing.Id
        );

    /// <summary>Saves, turning a lost race on the unique name index into the same error the pre-check gives.</summary>
    private async Task SaveAsync(Guid ownerId, Guid? folderId, NodeName name, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e)
            when (e.InnerException
                    is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
            )
        {
            db.ChangeTracker.Clear();
            var existing = await FindByNameAsync(ownerId, folderId, name, ct);
            throw existing is null ? e : NameTaken(existing);
        }
    }
}

/// <summary>Bytes in the blob store to record as a file in <see cref="FolderId"/>, or as a new version of <see cref="TargetNodeId"/>.</summary>
internal sealed record NewFile(
    Guid? FolderId,
    Guid? TargetNodeId,
    NodeName Name,
    ContentHash Hash,
    long Size,
    string MimeType,
    DateTimeOffset? ClientModifiedAt,
    NameConflictPolicy OnConflict
);

/// <summary>A folder of a folder upload: the one made for <see cref="Path"/>, or the existing one it was merged into.</summary>
/// <param name="ExistingNames">For a folder that was already there, the names of what it holds.</param>
public sealed record TreeFolder(string Path, Node Folder, bool Created, List<string> ExistingNames);

public sealed record AddedFile(Node Node, FileVersion Version, bool Created, bool NewVersion);

public sealed record FileContent(Node Node, FileVersion Version, string PhysicalPath);

/// <summary>The size of everything in a folder, counting each file's current version once, and how many files and folders that is.</summary>
public sealed record FolderContents(long Bytes, int Files, int Folders)
{
    public static readonly FolderContents Empty = new(0, 0, 0);
}
