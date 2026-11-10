using System.IO.Compression;
using System.Text.Json;
using Dokument.Domain.Files;
using Dokument.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dokument.Infrastructure.Backups;

/// <summary>
/// Writes the name manifest (docs/design.md §7.1): gzipped JSON lines, one per version that isn't purged, saying whose
/// it is, which folders it is in, its name, and which blob holds its bytes. With the blobs and a manifest, every file
/// can be put back with its name and folder without Postgres.
/// </summary>
/// <remarks>
/// A line: <c>{"owner":{"id","email","name"},"path":["Kvitton","2024"],"name":"kvitto.pdf","version":3,"current":true,
/// "trashed":false,"keepForever":false,"hash":"…","size":1234,"mimeType":"application/pdf",
/// "modifiedAt":"2026-11-10T03:00:00+00:00","blob":"blobs/{owner id}/ab/cd/abcd…"}</c>. <c>path</c> is the folders from
/// the top of the owner's My files, <c>blob</c> is relative to the originals folder, and <c>modifiedAt</c> is the
/// file's own modified time when the uploader's device sent one, else the upload time.
/// </remarks>
public sealed class NameManifest(DokumentDbContext db)
{
    /// <summary>Writes the manifest to <paramref name="path"/> and returns how many versions it lists.</summary>
    public async Task<int> WriteAsync(string path, CancellationToken ct)
    {
        // One snapshot, so a file moved halfway through is listed in one place.
        await using var transaction = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead,
            ct
        );
        var owners = await db
            .Users.AsNoTracking()
            .Select(u => new
            {
                u.Id,
                u.Email,
                u.DisplayName,
            })
            .ToDictionaryAsync(u => u.Id, ct);
        var folders = await db
            .Nodes.AsNoTracking()
            .Where(n => n.Kind == NodeKind.Folder)
            .Select(n => new
            {
                n.Id,
                n.ParentId,
                n.Name,
            })
            .ToDictionaryAsync(n => n.Id, ct);
        var paths = new Dictionary<Guid, string[]>();
        string[] PathOf(Guid? folderId)
        {
            if (folderId is not { } id || !folders.TryGetValue(id, out var folder))
            {
                return [];
            }

            if (!paths.TryGetValue(id, out var path))
            {
                path = [.. PathOf(folder.ParentId), folder.Name.Value];
                paths[id] = path;
            }

            return path;
        }

        var versions = db
            .FileVersions.AsNoTracking()
            .Join(db.Nodes, v => v.NodeId, n => n.Id, (v, n) => new { v, n })
            .OrderBy(x => x.n.OwnerId)
            .ThenBy(x => x.n.Id)
            .ThenBy(x => x.v.Number)
            .Select(x => new
            {
                x.n.OwnerId,
                x.n.ParentId,
                x.n.Name,
                x.v.Number,
                Current = x.n.CurrentVersionId == x.v.Id,
                Trashed = x.n.TrashedAt != null,
                x.v.KeepForever,
                x.v.BlobHash,
                x.v.Size,
                x.v.MimeType,
                ModifiedAt = x.v.ClientModifiedAt ?? x.v.UploadedAt,
            })
            .AsAsyncEnumerable();

        var count = 0;
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        await using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
        await using (var json = new Utf8JsonWriter(gzip))
        {
            await foreach (var version in versions.WithCancellation(ct))
            {
                var owner = owners[version.OwnerId];
                json.WriteStartObject();
                json.WriteStartObject("owner");
                json.WriteString("id", owner.Id);
                json.WriteString("email", owner.Email);
                json.WriteString("name", owner.DisplayName);
                json.WriteEndObject();
                json.WriteStartArray("path");
                foreach (var folder in PathOf(version.ParentId))
                {
                    json.WriteStringValue(folder);
                }

                json.WriteEndArray();
                json.WriteString("name", version.Name.Value);
                json.WriteNumber("version", version.Number);
                json.WriteBoolean("current", version.Current);
                json.WriteBoolean("trashed", version.Trashed);
                json.WriteBoolean("keepForever", version.KeepForever);
                json.WriteString("hash", version.BlobHash.Hex);
                json.WriteNumber("size", version.Size);
                json.WriteString("mimeType", version.MimeType);
                json.WriteString("modifiedAt", version.ModifiedAt);
                json.WriteString("blob", StorageKey.For(version.OwnerId, version.BlobHash).Value);
                json.WriteEndObject();
                await json.FlushAsync(ct);
                gzip.WriteByte((byte)'\n');
                json.Reset();
                count++;
            }
        }

        await transaction.CommitAsync(ct);
        return count;
    }
}
