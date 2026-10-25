using Dokument.Domain.Files;
using Dokument.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace Dokument.Infrastructure.Files;

/// <summary>
/// The originals volume (docs/design.md §7): in-flight uploads in <c>tmp/</c>, finished files content-addressed in
/// <c>blobs/</c>, one folder per owner. Both are on the same filesystem, so committing a blob is an atomic rename. Blobs
/// are write-once.
/// </summary>
public sealed class BlobStore(IOptions<StorageOptions> options)
{
    private const int BufferSize = 1 << 20;

    private string Root => options.Value.OriginalsPath;

    public string PathOf(StorageKey key) => Path.Combine(Root, key.Value);

    public string TempPathOf(Guid uploadId) => Path.Combine(Root, "tmp", uploadId.ToString("N"));

    public void CreateTemp(Guid uploadId)
    {
        Directory.CreateDirectory(Path.Combine(Root, "tmp"));
        using var _ = new FileStream(TempPathOf(uploadId), FileMode.CreateNew, FileAccess.Write);
    }

    /// <summary>
    /// Writes <paramref name="body"/> at <paramref name="offset"/>, accepting at most <paramref name="maxBytes"/>. Bytes that
    /// arrived before the client went away are kept, so the upload resumes from there. <c>Overflow</c> says the client sent
    /// more than it declared; those bytes are not written.
    /// </summary>
    public async Task<(long Written, bool Overflow)> AppendAsync(
        Guid uploadId,
        long offset,
        Stream body,
        long maxBytes,
        CancellationToken ct
    )
    {
        await using var file = new FileStream(
            TempPathOf(uploadId),
            FileMode.Open,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            useAsync: true
        );
        file.SetLength(offset);
        file.Position = offset;
        var buffer = new byte[BufferSize];
        long written = 0;
        try
        {
            int read;
            while ((read = await body.ReadAsync(buffer, ct)) > 0)
            {
                var room = (int)Math.Min(read, maxBytes - written);
                await file.WriteAsync(buffer.AsMemory(0, room), CancellationToken.None);
                written += room;
                if (room < read)
                {
                    return (written, true);
                }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException)
        {
            // The connection dropped mid-chunk. Keep what arrived; the client asks for the offset and carries on.
        }
        finally
        {
            await file.FlushAsync(CancellationToken.None);
        }

        return (written, false);
    }

    public async Task<ContentHash> HashTempAsync(Guid uploadId, CancellationToken ct)
    {
        await using var file = new FileStream(
            TempPathOf(uploadId),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            useAsync: true
        );
        return await ContentHash.ComputeAsync(file, ct);
    }

    /// <summary>
    /// Moves a finished upload into the owner's <c>blobs/</c> folder under its hash: fsync, then rename. If the owner
    /// already has the blob the temp file is dropped, because identical bytes are stored once per owner.
    /// </summary>
    public StorageKey Commit(Guid uploadId, Guid ownerId, ContentHash hash) =>
        MoveIn(TempPathOf(uploadId), StorageKey.For(ownerId, hash));

    /// <summary>
    /// Copies another owner's blob into <paramref name="ownerId"/>'s folder, for bytes they get without uploading them
    /// (a copy of a file shared with them). Copied to a temp file first, so the blob appears whole or not at all.
    /// </summary>
    public StorageKey CopyIn(StorageKey source, Guid ownerId, ContentHash hash)
    {
        var key = StorageKey.For(ownerId, hash);
        if (File.Exists(PathOf(key)))
        {
            return key;
        }

        var temp = TempPathOf(Guid.NewGuid());
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        File.Copy(PathOf(source), temp);
        return MoveIn(temp, key);
    }

    public bool Exists(StorageKey key) => File.Exists(PathOf(key));

    private StorageKey MoveIn(string temp, StorageKey key)
    {
        var target = PathOf(key);
        if (File.Exists(target))
        {
            File.Delete(temp);
            return key;
        }

        using (var file = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite))
        {
            file.Flush(flushToDisk: true);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            File.Move(temp, target);
        }
        catch (IOException) when (File.Exists(target))
        {
            // Another upload of the same bytes won the race; its copy is identical.
            File.Delete(temp);
        }

        return key;
    }

    public void DeleteTemp(Guid uploadId) => File.Delete(TempPathOf(uploadId));
}
