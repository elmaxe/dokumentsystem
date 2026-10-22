namespace Dokument.Domain.Files;

/// <summary>
/// One upload of a file (docs/design.md §6, §8.2). Versions are never rewritten: restoring an old one adds a new
/// version that points at the old blob.
/// </summary>
public sealed class FileVersion
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required Guid NodeId { get; init; }
    public required int Number { get; init; }
    public required ContentHash BlobHash { get; init; }
    public required long Size { get; init; }
    public required string MimeType { get; init; }

    /// <summary>The name the file had when this version was uploaded.</summary>
    public required NodeName OriginalName { get; init; }
    public required Guid UploadedBy { get; init; }
    public DateTimeOffset UploadedAt { get; init; }

    /// <summary>The file's mtime on the uploading device. Shown only; the bytes are never touched.</summary>
    public DateTimeOffset? ClientModifiedAt { get; init; }

    /// <summary>The version this one restored, if it was made by "Restore".</summary>
    public int? RestoredFrom { get; init; }

    /// <summary>Exempts the version from retention purging (§8.2). It still counts toward quota.</summary>
    public bool KeepForever { get; set; }
}
