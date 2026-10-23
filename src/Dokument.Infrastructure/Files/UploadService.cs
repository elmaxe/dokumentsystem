using System.Collections.Concurrent;
using Dokument.Application.Files;
using Dokument.Domain.Audit;
using Dokument.Domain.Files;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Dokument.Infrastructure.Files;

/// <summary>
/// Resumable uploads (docs/design.md §8.1). Quota is reserved when an upload starts. When the last byte arrives the
/// file is hashed, checked against the hash the client claimed, moved into the blob store and recorded as a file or
/// version, all before the final response.
/// </summary>
public sealed class UploadService(
    DokumentDbContext db,
    DriveService drive,
    BlobStore blobs,
    UploadLocks locks,
    TimeProvider time
)
{
    public async Task<Upload> CreateAsync(
        Actor actor,
        NewUpload request,
        CancellationToken ct = default
    )
    {
        using var activity = DokumentTelemetry.ActivitySource.StartActivity("upload create");
        var name = DriveService.ParseName(request.Name);
        if (request.Size < 0)
        {
            throw new DriveException(DriveError.InvalidRequest, "The size can't be negative.");
        }

        Guid ownerId;
        if (request.TargetNodeId is { } targetId)
        {
            ownerId = (await drive.GetFileAsync(actor, targetId, ct)).OwnerId;
        }
        else
        {
            var parent = await drive.GetFolderAsync(actor, request.FolderId, ct);
            ownerId = parent?.OwnerId ?? actor.UserId;
            if (request.OnConflict == NameConflictPolicy.Fail)
            {
                // Checked again on completion; asking now saves sending the bytes first.
                await drive.EnsureNameFreeAsync(ownerId, request.FolderId, name, except: null, ct);
            }
        }

        var now = time.GetUtcNow();
        var upload = new Upload
        {
            UserId = actor.UserId,
            OwnerId = ownerId,
            ParentId = request.TargetNodeId is null ? request.FolderId : null,
            TargetNodeId = request.TargetNodeId,
            Name = name,
            Size = request.Size,
            ClaimedHash = request.Sha256,
            MimeType = MimeTypes.For(name.Value, request.MimeType),
            ClientModifiedAt = request.ClientModifiedAt,
            OnConflict = request.OnConflict,
            CreatedAt = now,
            ExpiresAt = now + Upload.Lifetime,
        };

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await drive.ReserveQuotaAsync(ownerId, upload.Size, ct);
            db.Uploads.Add(upload);
            await db.SaveChangesAsync(ct);
            blobs.CreateTemp(upload.Id);
            await tx.CommitAsync(ct);
        }

        activity?.SetTag("dokument.upload.id", upload.Id);
        activity?.SetTag("dokument.upload.size", upload.Size);
        if (upload.Size == 0)
        {
            await CompleteAsync(actor, upload, ct);
        }

        return upload;
    }

    /// <summary>The caller's own upload. Expired, unfinished uploads are gone even before the cleanup removes them.</summary>
    public async Task<Upload> GetAsync(Actor actor, Guid id, CancellationToken ct = default)
    {
        var upload = await db.Uploads.SingleOrDefaultAsync(
            u => u.Id == id && u.UserId == actor.UserId,
            ct
        );
        return upload is null || (!upload.IsComplete && upload.ExpiresAt <= time.GetUtcNow())
            ? throw DriveException.NotFound("upload")
            : upload;
    }

    /// <summary>Appends a chunk at <paramref name="offset"/>, which must be where the upload left off.</summary>
    public async Task<Upload> AppendAsync(
        Actor actor,
        Guid id,
        long offset,
        Stream body,
        CancellationToken ct = default
    )
    {
        using var activity = DokumentTelemetry.ActivitySource.StartActivity("upload append");
        activity?.SetTag("dokument.upload.id", id);
        using var held =
            locks.TryEnter(id)
            ?? throw new DriveException(
                DriveError.UploadBusy,
                "Another request is already writing to this upload."
            );
        var upload = await GetAsync(actor, id, ct);
        if (upload.IsComplete)
        {
            throw new DriveException(DriveError.UploadMismatch, "The upload is already complete.");
        }

        if (offset != upload.Offset)
        {
            throw new DriveException(
                DriveError.UploadMismatch,
                $"The upload is at offset {upload.Offset}, not {offset}."
            );
        }

        var (written, overflow) = await blobs.AppendAsync(
            upload.Id,
            offset,
            body,
            upload.Size - offset,
            ct
        );
        upload.Offset += written;
        activity?.SetTag("dokument.upload.bytes", written);
        DokumentTelemetry.UploadBytes.Add(written);
        upload.ExpiresAt = time.GetUtcNow() + Upload.Lifetime;

        // Saved even if the client went away, so the next HEAD reports the bytes that did arrive.
        await db.SaveChangesAsync(CancellationToken.None);
        if (overflow)
        {
            throw new DriveException(
                DriveError.InvalidRequest,
                "The upload sent more bytes than its declared length."
            );
        }

        if (upload.AllBytesReceived)
        {
            ct.ThrowIfCancellationRequested();
            await CompleteAsync(actor, upload, ct);
        }

        return upload;
    }

    public async Task TerminateAsync(Actor actor, Guid id, CancellationToken ct = default)
    {
        using var held =
            locks.TryEnter(id)
            ?? throw new DriveException(
                DriveError.UploadBusy,
                "Another request is already writing to this upload."
            );
        var upload = await GetAsync(actor, id, ct);
        if (upload.IsComplete)
        {
            throw new DriveException(DriveError.UploadMismatch, "The upload is already complete.");
        }

        await DiscardAsync(upload, ct);
    }

    /// <summary>
    /// Deletes abandoned uploads and their temp files and releases their reservations (§8.1), and forgets finished
    /// uploads whose result the client has had a day to read. Returns how many were removed.
    /// </summary>
    public async Task<int> ExpireAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var expired = await db.Uploads.Where(u => u.ExpiresAt <= now).ToListAsync(ct);
        foreach (var upload in expired)
        {
            using var held = locks.TryEnter(upload.Id);
            if (held is null)
            {
                continue;
            }

            if (upload.IsComplete)
            {
                db.Uploads.Remove(upload);
                await db.SaveChangesAsync(ct);
            }
            else
            {
                await DiscardAsync(upload, ct);
            }
        }

        return expired.Count;
    }

    private async Task CompleteAsync(Actor actor, Upload upload, CancellationToken ct)
    {
        using var activity = DokumentTelemetry.ActivitySource.StartActivity("upload finish");
        activity?.SetTag("dokument.upload.id", upload.Id);
        ContentHash hash;
        using (var hashing = DokumentTelemetry.ActivitySource.StartActivity("upload hash"))
        {
            hashing?.SetTag("dokument.upload.size", upload.Size);
            hash = await blobs.HashTempAsync(upload.Id, ct);
        }

        if (upload.ClaimedHash is not null && upload.ClaimedHash != hash)
        {
            await DiscardAsync(upload, ct);
            throw new DriveException(
                DriveError.UploadMismatch,
                "The file changed while it was being uploaded: its SHA-256 doesn't match. Upload it again."
            );
        }

        blobs.Commit(upload.Id, hash);
        AddedFile added;
        try
        {
            added = await drive.AddFileAsync(
                actor,
                new NewFile(
                    upload.ParentId,
                    upload.TargetNodeId,
                    upload.Name,
                    hash,
                    upload.Size,
                    upload.MimeType ?? MimeTypes.Default,
                    upload.ClientModifiedAt,
                    upload.OnConflict
                ),
                reservedBytes: upload.Size,
                AuditActions.FileUpload,
                ct
            );
        }
        catch (DriveException)
        {
            // E.g. the folder was removed or the name taken meanwhile. The blob stays for the integrity job to collect.
            db.ChangeTracker.Clear();
            await DiscardAsync(upload, ct);
            throw;
        }

        upload.ResultNodeId = added.Node.Id;
        activity?.SetTag("dokument.node.id", added.Node.Id);
        upload.CompletedAt = time.GetUtcNow();
        upload.ExpiresAt = upload.CompletedAt.Value + Upload.Lifetime;
        await db.SaveChangesAsync(ct);
    }

    private async Task DiscardAsync(Upload upload, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await drive.SettleQuotaAsync(upload.OwnerId, reservedBytes: upload.Size, addedBytes: 0, ct);
        await db.Uploads.Where(u => u.Id == upload.Id).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        if (db.Entry(upload).State != EntityState.Detached)
        {
            db.Entry(upload).State = EntityState.Detached;
        }

        blobs.DeleteTemp(upload.Id);
    }
}

/// <summary>What a client asks for when it starts an upload.</summary>
public sealed record NewUpload(
    string? Name,
    long Size,
    Guid? FolderId = null,
    Guid? TargetNodeId = null,
    ContentHash? Sha256 = null,
    string? MimeType = null,
    DateTimeOffset? ClientModifiedAt = null,
    NameConflictPolicy OnConflict = NameConflictPolicy.Fail
);

/// <summary>One writer per upload at a time. A single server process owns the uploads, so an in-memory lock is enough.</summary>
public sealed class UploadLocks
{
    private readonly ConcurrentDictionary<Guid, byte> held = new();

    public IDisposable? TryEnter(Guid id) => held.TryAdd(id, 0) ? new Release(held, id) : null;

    private sealed class Release(ConcurrentDictionary<Guid, byte> held, Guid id) : IDisposable
    {
        public void Dispose() => held.TryRemove(id, out _);
    }
}
