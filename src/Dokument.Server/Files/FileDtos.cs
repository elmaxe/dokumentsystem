using Dokument.Domain.Files;
using Dokument.Domain.Previews;
using Dokument.Domain.Tags;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Jobs;
using Dokument.Infrastructure.Tags;

namespace Dokument.Server.Files;

/// <param name="Size">Bytes of the current version; for a folder, the current versions of everything in it.</param>
/// <param name="MimeType">Of the current version. Null for folders.</param>
/// <param name="Sha256">The current version's content hash, in hex. Null for folders.</param>
/// <param name="Thumbnail">Whether <c>/nodes/{id}/thumbnail</c> has an image yet. Pending means the worker is on it; poll or wait.</param>
/// <param name="Contents">How many files and folders a folder holds, at any depth. Null for files.</param>
/// <param name="Starred">Whether the person asking starred it. Stars are each person's own.</param>
/// <param name="Processing">
/// The steps the worker still has to do for the current version, in pipeline order; empty when it is done. Files with
/// any show a "Processing" badge. See <c>/nodes/{id}/processing</c> for every step.
/// </param>
/// <param name="Tags">The file's tags, by name: the ones people added and the automatic ones nobody removed (§13.4).</param>
/// <param name="Media">For video and audio files, once the worker has looked at them. Null for everything else.</param>
public sealed record NodeDto(
    Guid Id,
    Guid? ParentId,
    NodeKind Kind,
    string Name,
    long? Size,
    string? MimeType,
    string? Sha256,
    Guid OwnerId,
    string OwnerName,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt,
    Guid? CurrentVersionId,
    int? VersionNumber,
    ThumbnailState Thumbnail,
    FolderCounts? Contents,
    bool Starred,
    List<ActiveStepDto> Processing,
    List<TagDto> Tags,
    MediaDto? Media
)
{
    public static NodeDto From(
        Node node,
        IReadOnlyDictionary<Guid, string> ownerNames,
        IReadOnlyDictionary<ContentHash, ThumbnailState> thumbnails,
        IReadOnlyDictionary<Guid, FolderContents> folders,
        IReadOnlySet<Guid> starred,
        IReadOnlyDictionary<ContentHash, List<ActiveStep>> processing,
        IReadOnlyDictionary<Guid, List<TagView>> tags,
        IReadOnlyDictionary<ContentHash, MediaState> media
    ) =>
        new(
            node.Id,
            node.ParentId,
            node.Kind,
            node.Name.Value,
            folders.TryGetValue(node.Id, out var contents)
                ? contents.Bytes
                : node.CurrentVersion?.Size,
            node.CurrentVersion?.MimeType,
            node.CurrentVersion?.BlobHash.Hex,
            node.OwnerId,
            ownerNames.GetValueOrDefault(node.OwnerId, ""),
            node.CreatedAt,
            node.ModifiedAt,
            node.CurrentVersionId,
            node.CurrentVersion?.Number,
            node.CurrentVersion is { } version
                ? thumbnails.GetValueOrDefault(version.BlobHash, ThumbnailState.None)
                : ThumbnailState.None,
            contents is null ? null : new FolderCounts(contents.Files, contents.Folders),
            starred.Contains(node.Id),
            node.CurrentVersion is { } current
            && processing.TryGetValue(current.BlobHash, out var steps)
                ? steps.Select(s => new ActiveStepDto(JobKinds.StepOf(s.Type), s.Running)).ToList()
                : [],
            tags.TryGetValue(node.Id, out var shown)
                ? shown.Select(t => new TagDto(t.Name, t.Source, t.RuleId)).ToList()
                : [],
            node.CurrentVersion is { } latest && media.TryGetValue(latest.BlobHash, out var m)
                ? new MediaDto(
                    m.DurationSeconds,
                    m.HasVideo,
                    m.HasAudio,
                    m.VideoCodec,
                    m.Copy,
                    m.Progress
                )
                : null
        );
}

/// <summary>A processing step that hasn't finished yet.</summary>
/// <param name="Running">False while it waits its turn.</param>
public sealed record ActiveStepDto(FileStep Step, bool Running);

/// <param name="Source">Who added it: a person (<c>user</c>), a keyword rule or the ML service. Show automatic ones differently.</param>
/// <param name="RuleId">For <c>rule</c>: which keyword rule added it.</param>
public sealed record TagDto(string Name, TagSource Source, Guid? RuleId);

/// <param name="Tags">What the file shows.</param>
/// <param name="Removed">Automatic tags someone took off this file, which "Put back" can restore.</param>
public sealed record FileTags(List<TagDto> Tags, List<string> Removed);

/// <param name="Ids">Files to change, 1 to 1000.</param>
/// <param name="Add">Tags to add, at most 20. Adding a removed automatic tag puts it back.</param>
/// <param name="Remove">Tags to remove, at most 20.</param>
public sealed record ChangeTagsRequest(List<Guid> Ids, List<string>? Add, List<string>? Remove);

/// <param name="Files">How many of the person's files have it.</param>
public sealed record TagSuggestion(string Name, int Files);

public sealed record TagSuggestions(List<TagSuggestion> Tags);

/// <summary>A video or audio file, as the tile and the viewer need it (docs/design.md §14).</summary>
/// <param name="DurationSeconds">Null when the file doesn't say, e.g. some live recordings.</param>
/// <param name="HasVideo">False for audio files; their cover art, if any, is the thumbnail.</param>
/// <param name="VideoCodec">ffprobe's name, e.g. <c>hevc</c>: the viewer tries the original first when the browser says it plays it.</param>
/// <param name="Copy">Whether <c>/nodes/{id}/preview</c> serves a playable copy, or will. NotNeeded means the original plays as it is.</param>
/// <param name="Progress">How far a copy being made has got, 0 to 100.</param>
public sealed record MediaDto(
    double? DurationSeconds,
    bool HasVideo,
    bool HasAudio,
    string? VideoCodec,
    CopyState Copy,
    int? Progress
);

/// <summary>What a folder holds, at any depth.</summary>
public sealed record FolderCounts(int Files, int Folders);

/// <summary>One step of a breadcrumb trail, from the top of "My files" down.</summary>
public sealed record PathEntry(Guid Id, string Name);

/// <param name="Folder">Null for the top of "My files".</param>
/// <param name="Path">The folders above <paramref name="Folder"/> and the folder itself, top first.</param>
public sealed record FolderListing(NodeDto? Folder, List<PathEntry> Path, List<NodeDto> Items);

/// <param name="Path">The folders above the node, top first.</param>
public sealed record NodeDetails(NodeDto Node, List<PathEntry> Path);

public sealed record CreateFolderRequest(Guid? FolderId, string Name);

/// <param name="FolderId">Where the uploaded folders go; null for the top of "My files".</param>
/// <param name="Paths">Each folder to make, relative to <paramref name="FolderId"/>, its names separated by "/" ("Kvitton/2025"). Folders above a path are made too.</param>
/// <param name="OnConflict">What to do when a top folder's name is taken. Folders below a merged one are always merged.</param>
/// <param name="TotalBytes">The size of all the files that will be uploaded into these folders. Nothing is made if it doesn't fit in the quota.</param>
public sealed record FolderTreeRequest(
    Guid? FolderId,
    List<string> Paths,
    FolderConflictPolicy OnConflict = FolderConflictPolicy.Fail,
    long TotalBytes = 0
);

/// <param name="Path">The path as it was asked for.</param>
/// <param name="Created">False when an existing folder was reused (merged into).</param>
/// <param name="ExistingNames">What an existing folder already holds, to ask about name clashes before uploading. Empty for new folders.</param>
public sealed record FolderTreeEntry(
    string Path,
    Guid Id,
    string Name,
    bool Created,
    List<string> ExistingNames
);

public sealed record FolderTreeResponse(List<FolderTreeEntry> Folders);

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
    DateTimeOffset? PurgeAt
);

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
public sealed record CopyFromContentRequest(
    Guid? FolderId,
    string Name,
    string Sha256,
    DateTimeOffset? ClientModifiedAt,
    NameConflictPolicy OnConflict = NameConflictPolicy.Fail
);

/// <param name="NodeId">The file the upload produced, once complete.</param>
/// <param name="Duplicates">Once complete: other files with the same content, for clients that couldn't check before uploading (§8.3).</param>
public sealed record UploadStatus(
    Guid Id,
    string Name,
    long Size,
    long Offset,
    bool Complete,
    Guid? NodeId,
    DateTimeOffset ExpiresAt,
    List<DuplicateMatch> Duplicates
);

/// <param name="QuotaBytes">Null means unlimited.</param>
/// <param name="TrashBytes">The part of <paramref name="UsedBytes"/> that emptying the trash would free.</param>
public sealed record StorageUsage(long UsedBytes, long? QuotaBytes, long TrashBytes);

public sealed record NodeIdsRequest(List<Guid> Ids);

/// <param name="Path">Where the item was: the folders above it, top first. Empty for the top of My files.</param>
/// <param name="DeleteAt">When the purge job deletes it forever (§12).</param>
/// <param name="ItemCount">For a folder, how many things went to the trash with it.</param>
/// <param name="Size">Bytes of the current versions, a folder's contents included.</param>
public sealed record TrashItem(
    NodeDto Node,
    List<PathEntry> Path,
    DateTimeOffset TrashedAt,
    DateTimeOffset DeleteAt,
    int ItemCount,
    long Size
);

/// <param name="FreeableBytes">The storage that emptying the trash would free.</param>
/// <param name="KeepDays">How long the trash keeps items, as the admin set it.</param>
public sealed record TrashListing(List<TrashItem> Items, long FreeableBytes, int KeepDays);

/// <param name="MovedToRoot">Its folder is in the trash too, so it went to the top of My files instead.</param>
public sealed record RestoredItem(NodeDto Node, bool MovedToRoot);

public sealed record RestoreResponse(List<RestoredItem> Items);

public sealed record EmptyTrashResponse(int Items);

/// <param name="Path">The folders above the item, top first. Empty for the top of My files.</param>
public sealed record StarredItem(NodeDto Node, List<PathEntry> Path);

public sealed record StarredListing(List<StarredItem> Items);

/// <param name="Path">The folders above the file, top first. Empty for the top of My files.</param>
/// <param name="At">When the person last opened, downloaded, uploaded or restored it.</param>
/// <param name="Action">Which of those it was.</param>
public sealed record RecentItem(
    NodeDto Node,
    List<PathEntry> Path,
    DateTimeOffset At,
    RecentAction Action
);

/// <param name="Items">Newest first, at most 100.</param>
public sealed record RecentListing(List<RecentItem> Items);
