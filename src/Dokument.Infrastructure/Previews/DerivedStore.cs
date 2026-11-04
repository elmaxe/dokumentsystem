using Dokument.Domain.Previews;
using Dokument.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace Dokument.Infrastructure.Previews;

/// <summary>
/// The derived volume (docs/design.md §7): thumbnails and preview renditions. Everything here can be regenerated from
/// the originals, so backing it up is optional.
/// </summary>
public sealed class DerivedStore(IOptions<StorageOptions> options)
{
    private string Root => options.Value.DerivedPath;

    public string PathOf(DerivedKey key) => Path.Combine(Root, key.Value);

    /// <summary>Writes a rendition in one step: to a temp file beside it, then a rename, so readers never see half a file.</summary>
    public async Task WriteAsync(DerivedKey key, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        var target = PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = $"{target}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, ct);
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public void Delete(DerivedKey key) => File.Delete(PathOf(key));

    /// <summary>
    /// Where the worker writes the PDFs of a merge or split (docs/design.md §8.4) for the server to pick up. On the
    /// derived volume, because the worker can't write originals (§10.4).
    /// </summary>
    public string PdfWorkPath(Guid operationId) =>
        Path.Combine(Root, "tmp", "pdf", operationId.ToString("N"));

    public void DeletePdfWork(Guid operationId)
    {
        var path = PdfWorkPath(operationId);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
