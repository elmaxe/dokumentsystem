using Dokument.Domain.Files;

namespace Dokument.Server.Files;

/// <param name="Size">Bytes of the current version. Null for folders.</param>
/// <param name="MimeType">Of the current version. Null for folders.</param>
public sealed record NodeDto(
    Guid Id,
    Guid? ParentId,
    NodeKind Kind,
    string Name,
    long? Size,
    string? MimeType,
    Guid OwnerId,
    string OwnerName,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt,
    Guid? CurrentVersionId,
    int? VersionNumber)
{
    public static NodeDto From(Node node, IReadOnlyDictionary<Guid, string> ownerNames) => new(
        node.Id,
        node.ParentId,
        node.Kind,
        node.Name.Value,
        node.CurrentVersion?.Size,
        node.CurrentVersion?.MimeType,
        node.OwnerId,
        ownerNames.GetValueOrDefault(node.OwnerId, ""),
        node.CreatedAt,
        node.ModifiedAt,
        node.CurrentVersionId,
        node.CurrentVersion?.Number);
}

/// <summary>One step of a breadcrumb trail, from the top of "My files" down.</summary>
public sealed record PathEntry(Guid Id, string Name);

/// <param name="Folder">Null for the top of "My files".</param>
/// <param name="Path">The folders above <paramref name="Folder"/> and the folder itself, top first.</param>
public sealed record FolderListing(NodeDto? Folder, List<PathEntry> Path, List<NodeDto> Items);

/// <param name="Path">The folders above the node, top first.</param>
public sealed record NodeDetails(NodeDto Node, List<PathEntry> Path);

public sealed record CreateFolderRequest(Guid? FolderId, string Name);

public sealed record RenameRequest(string Name);

/// <param name="FolderId">The folder to move into; null for the top of "My files".</param>
public sealed record MoveRequest(Guid? FolderId);

/// <param name="PurgeAt">When retention removes this version; null if it is kept (current or Keep forever).</param>
public sealed record VersionDto(
    Guid Id,
    int Number,
    long Size,
    string MimeType,
    string OriginalName,
    Guid UploadedBy,
    string UploadedByName,
    DateTimeOffset UploadedAt,
    DateTimeOffset? ClientModifiedAt,
    bool KeepForever,
    bool Current,
    int? RestoredFrom,
    DateTimeOffset? PurgeAt);

public sealed record KeepForeverRequest(bool KeepForever);

/// <param name="Sha256">Lowercase hex, computed on the device before uploading.</param>
public sealed record DuplicateCheckItem(string Name, long Size, string Sha256);

public sealed record DuplicateCheckRequest(List<DuplicateCheckItem> Files);

/// <param name="Path">The folders the file is in, top first, e.g. "Kvitton / 2025". Empty for the top of "My files".</param>
public sealed record DuplicateMatch(Guid NodeId, string Name, List<PathEntry> Path, bool InTrash);

/// <param name="Duplicates">Files the user can already see with the same content. Empty means the file is new.</param>
public sealed record DuplicateCheckResult(string Sha256, List<DuplicateMatch> Duplicates);

public sealed record DuplicateCheckResponse(List<DuplicateCheckResult> Files);

/// <summary>"Upload anyway" for content the user already has: a new file pointing at the same bytes, with nothing sent.</summary>
public sealed record CopyFromContentRequest(Guid? FolderId, string Name, string Sha256, DateTimeOffset? ClientModifiedAt, NameConflictPolicy OnConflict = NameConflictPolicy.Fail);

/// <param name="NodeId">The file the upload produced, once complete.</param>
/// <param name="Duplicates">Once complete: other files with the same content, for clients that couldn't check before uploading (§8.3).</param>
public sealed record UploadStatus(Guid Id, string Name, long Size, long Offset, bool Complete, Guid? NodeId, DateTimeOffset ExpiresAt, List<DuplicateMatch> Duplicates);

/// <param name="QuotaBytes">Null means unlimited.</param>
public sealed record StorageUsage(long UsedBytes, long? QuotaBytes);
