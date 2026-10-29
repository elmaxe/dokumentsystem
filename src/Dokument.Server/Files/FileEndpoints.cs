using System.Security.Claims;
using Dokument.Application.Files;
using Dokument.Domain.Files;
using Dokument.Domain.Jobs;
using Dokument.Domain.Previews;
using Dokument.Domain.Settings;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Jobs;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Previews;
using Dokument.Infrastructure.Settings;
using Dokument.Infrastructure.Telemetry;
using Dokument.Server.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Dokument.Server.Files;

/// <summary>Folders, files, versions, downloads and the duplicate check (docs/design.md §6, §8).</summary>
public static class FileEndpoints
{
    public static RouteGroupBuilder MapFileEndpoints(this RouteGroupBuilder api)
    {
        var files = api.MapGroup("").WithTags("Files").RequireAuthorization();
        files
            .MapGet("/nodes", ListFolder)
            .WithName("ListFolder")
            .WithDescription(
                "The items in a folder, or at the top of My files when folderId is left out."
            );
        files.MapGet("/nodes/{id:guid}", GetNode).WithName("GetNode");
        files.MapPost("/folders", CreateFolder).WithName("CreateFolder");
        files
            .MapPost("/folders/tree", CreateFolderTree)
            .WithName("CreateFolderTree")
            .WithDescription(
                "Folder uploads: makes all the folders first, in one go, then the files are uploaded into them."
            );
        files.MapPost("/nodes/{id:guid}/rename", Rename).WithName("RenameNode");
        files.MapPost("/nodes/{id:guid}/move", Move).WithName("MoveNode");
        files
            .MapGet("/nodes/{id:guid}/content", Download)
            .WithName("DownloadFile")
            .WithDescription(
                "The original bytes, as an attachment. Supports ranges. Audited, unlike previews."
            );
        files
            .MapGet("/nodes/{id:guid}/thumbnail", GetThumbnail)
            .WithName("GetThumbnail")
            .WithDescription(
                "A WebP thumbnail of the file: small (256 px) for lists, medium (640 px) for the grid. 404 until the worker has made one; see NodeDto.thumbnail."
            );
        files
            .MapGet("/nodes/{id:guid}/preview", GetPreview)
            .WithName("GetPreview")
            .WithDescription(
                "What the viewer shows: a WebP for images browsers can't display or that are huge, the playable copy of a video or audio file browsers can't play (unless original=true), the PDF made of an Office document, otherwise the original bytes, inline. Supports ranges. Not audited, unlike downloads (§18)."
            );
        files
            .MapPost("/nodes/{id:guid}/playable", RequestPlayable)
            .WithName("RequestPlayableCopy")
            .WithDescription(
                "Asks for a playable copy of a video or audio file browsers can't play, ahead of other work. 409 when copies are turned off or the file can't be converted; see NodeDto.media."
            );
        files.MapGet("/nodes/{id:guid}/versions", ListVersions).WithName("ListVersions");
        files
            .MapGet("/nodes/{id:guid}/processing", GetProcessing)
            .WithName("GetProcessing")
            .WithDescription(
                "Where the worker is with the file's current version: thumbnail, metadata and text. Admins also get each failed step's job and error."
            );
        files
            .MapPost("/nodes/{id:guid}/versions/{versionId:guid}/restore", RestoreVersion)
            .WithName("RestoreVersion");
        files
            .MapPut("/nodes/{id:guid}/versions/{versionId:guid}/keep-forever", SetKeepForever)
            .WithName("SetKeepForever");
        files
            .MapPost("/uploads/check", CheckDuplicates)
            .WithName("CheckDuplicates")
            .WithDescription(
                "Run before uploading: which of these files (by SHA-256) the user already has."
            );
        files
            .MapPost("/uploads/copy", CopyFromContent)
            .WithName("CopyFromContent")
            .WithDescription(
                "Upload anyway: adds a file with content the user already has, without sending the bytes."
            );
        files.MapGet("/storage", GetStorage).WithName("GetStorage");
        return api;
    }

    public static Actor Actor(this HttpContext context) =>
        new(
            context.User.UserId(),
            context.Connection.RemoteIpAddress?.ToString(),
            context.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null
        );

    private static async Task<Ok<FolderListing>> ListFolder(
        Guid? folderId,
        HttpContext context,
        DriveService drive,
        NodeDtoMapper mapper,
        CancellationToken ct
    )
    {
        var actor = context.Actor();
        var folder = await drive.GetFolderAsync(actor, folderId, ct);
        var items = await drive.ListChildrenAsync(actor, folderId, ct);
        var path = folder is null
            ? []
            : await PathOfAsync(drive, folder, actor, includeSelf: true, ct);
        var dtos = await mapper.MapAsync(folder is null ? items : [.. items, folder], ct);
        return TypedResults.Ok(
            new FolderListing(folder is null ? null : dtos[^1], path, dtos[..items.Count])
        );
    }

    private static async Task<Ok<NodeDetails>> GetNode(
        Guid id,
        HttpContext context,
        DriveService drive,
        NodeDtoMapper mapper,
        CancellationToken ct
    )
    {
        var actor = context.Actor();
        var node = await drive.GetAsync(actor, id, ct);
        return TypedResults.Ok(
            new NodeDetails(
                await mapper.MapAsync(node, ct),
                await PathOfAsync(drive, node, actor, includeSelf: false, ct)
            )
        );
    }

    private static async Task<Ok<FileProcessing>> GetProcessing(
        Guid id,
        HttpContext context,
        DriveService drive,
        JobMonitor jobs,
        CancellationToken ct
    )
    {
        var node = await drive.GetFileAsync(context.Actor(), id, ct);
        var steps = await jobs.StepsAsync(node.CurrentVersion!.BlobHash, ct);
        var admin = context.User.IsInRole("admin");
        return TypedResults.Ok(
            new FileProcessing(steps.Select(s => ProcessingStepDto.From(s, admin)).ToList())
        );
    }

    private static async Task<Created<NodeDto>> CreateFolder(
        CreateFolderRequest request,
        HttpContext context,
        DriveService drive,
        NodeDtoMapper mapper,
        CancellationToken ct
    )
    {
        var folder = await drive.CreateFolderAsync(
            context.Actor(),
            request.FolderId,
            request.Name,
            ct
        );
        return TypedResults.Created(
            $"/api/v1/nodes/{folder.Id}",
            await mapper.MapAsync(folder, ct)
        );
    }

    private static async Task<Results<Ok<FolderTreeResponse>, ProblemHttpResult>> CreateFolderTree(
        FolderTreeRequest request,
        HttpContext context,
        DriveService drive,
        CancellationToken ct
    )
    {
        if (request.Paths.Count is 0 or > 10_000)
        {
            return TypedResults.Problem(
                "Send between 1 and 10 000 folder paths.",
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        var folders = await drive.CreateFolderTreeAsync(
            context.Actor(),
            request.FolderId,
            request.Paths,
            request.OnConflict,
            request.TotalBytes,
            ct
        );
        return TypedResults.Ok(
            new FolderTreeResponse(
                folders
                    .Select(f => new FolderTreeEntry(
                        f.Path,
                        f.Folder.Id,
                        f.Folder.Name.Value,
                        f.Created,
                        f.ExistingNames
                    ))
                    .ToList()
            )
        );
    }

    private static async Task<Ok<NodeDto>> Rename(
        Guid id,
        RenameRequest request,
        HttpContext context,
        DriveService drive,
        NodeDtoMapper mapper,
        CancellationToken ct
    )
    {
        var node = await drive.RenameAsync(context.Actor(), id, request.Name, ct);
        return TypedResults.Ok(await mapper.MapAsync(node, ct));
    }

    private static async Task<Ok<NodeDto>> Move(
        Guid id,
        MoveRequest request,
        HttpContext context,
        DriveService drive,
        NodeDtoMapper mapper,
        CancellationToken ct
    )
    {
        var node = await drive.MoveAsync(context.Actor(), id, request.FolderId, ct);
        return TypedResults.Ok(await mapper.MapAsync(node, ct));
    }

    private static async Task<PhysicalFileHttpResult> Download(
        Guid id,
        Guid? versionId,
        HttpContext context,
        DriveService drive,
        CancellationToken ct
    )
    {
        // A resumed or seeking download sends a Range; only the request that starts at the first byte is audited.
        var range = context.Request.Headers.Range.ToString();
        var audited = range.Length == 0 || range.StartsWith("bytes=0-", StringComparison.Ordinal);
        var content = await drive.OpenContentAsync(context.Actor(), id, versionId, audited, ct);
        if (audited)
        {
            DokumentTelemetry.Downloads.Add(1);
        }

        // User content never renders in our origin (§21).
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
        return TypedResults.PhysicalFile(
            content.PhysicalPath,
            content.Version.MimeType,
            fileDownloadName: content.Node.Name.Value,
            lastModified: content.Version.UploadedAt,
            entityTag: new EntityTagHeaderValue($"\"{content.Version.BlobHash.Hex}\""),
            enableRangeProcessing: true
        );
    }

    /// <param name="size"><c>small</c> (256 px, for lists) or <c>medium</c> (640 px, for the grid; the default).</param>
    private static async Task<
        Results<PhysicalFileHttpResult, NotFound, ProblemHttpResult>
    > GetThumbnail(
        Guid id,
        Guid? versionId,
        string? size,
        HttpContext context,
        DriveService drive,
        PreviewService previews,
        CancellationToken ct
    )
    {
        RenditionKind kind;
        switch (size)
        {
            case null or "medium":
                kind = RenditionKind.ThumbMedium;
                break;
            case "small":
                kind = RenditionKind.ThumbSmall;
                break;
            default:
                return TypedResults.Problem(
                    "size is small or medium.",
                    statusCode: StatusCodes.Status400BadRequest
                );
        }

        var content = await drive.OpenContentAsync(
            context.Actor(),
            id,
            versionId,
            audited: false,
            ct
        );
        return
            await previews.FindAsync(content.Version.BlobHash, kind, ct) is { } thumbnail
            && File.Exists(thumbnail.PhysicalPath)
            ? Rendition(context, thumbnail)
            : TypedResults.NotFound();
    }

    /// <param name="original">For video and audio: the original even when a playable copy exists, for browsers that play more than most (HEVC in Safari).</param>
    private static async Task<PhysicalFileHttpResult> GetPreview(
        Guid id,
        Guid? versionId,
        bool? original,
        HttpContext context,
        DriveService drive,
        PreviewService previews,
        CancellationToken ct
    )
    {
        var content = await drive.OpenContentAsync(
            context.Actor(),
            id,
            versionId,
            audited: false,
            ct
        );
        if (
            await previews.FindAsync(content.Version.BlobHash, RenditionKind.Preview, ct)
                is { } preview
            && File.Exists(preview.PhysicalPath)
        )
        {
            return Rendition(context, preview);
        }

        if (
            original != true
            && await previews.FindAsync(content.Version.BlobHash, RenditionKind.Playable, ct)
                is { } playable
            && File.Exists(playable.PhysicalPath)
        )
        {
            return Rendition(context, playable);
        }

        if (
            await previews.FindAsync(content.Version.BlobHash, RenditionKind.Document, ct)
                is { } document
            && File.Exists(document.PhysicalPath)
        )
        {
            return Rendition(context, document);
        }

        // User content never renders in our origin (§21). Markup is shown as text, so a preview can't run it even sandboxed.
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
        context.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline")
        {
            FileNameStar = content.Node.Name.Value,
        }.ToString();
        context.Response.Headers.CacheControl = "private, no-cache";
        return TypedResults.PhysicalFile(
            content.PhysicalPath,
            MimeTypes.ForPreview(content.Version.MimeType),
            lastModified: content.Version.UploadedAt,
            entityTag: new EntityTagHeaderValue($"\"{content.Version.BlobHash.Hex}\""),
            enableRangeProcessing: true
        );
    }

    private static PhysicalFileHttpResult Rendition(HttpContext context, RenditionFile file)
    {
        // Renditions are keyed by content, so one only changes when the pipeline improves. A day's caching is plenty.
        context.Response.Headers.CacheControl = "private, max-age=86400";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return TypedResults.PhysicalFile(
            file.PhysicalPath,
            file.Rendition.MimeType,
            lastModified: file.Rendition.CreatedAt,
            entityTag: new EntityTagHeaderValue(
                $"\"{file.Rendition.BlobHash.Hex}-{file.Rendition.Kind.Key()}-{file.Rendition.CreatedAt.ToUnixTimeSeconds()}\""
            ),
            // Video players seek with ranges.
            enableRangeProcessing: true
        );
    }

    private static async Task<Results<Ok<NodeDto>, ProblemHttpResult>> RequestPlayable(
        Guid id,
        HttpContext context,
        DriveService drive,
        MediaService media,
        SettingsStore settings,
        DokumentDbContext db,
        NodeDtoMapper mapper,
        CancellationToken ct
    )
    {
        var node = await drive.GetFileAsync(context.Actor(), id, ct);
        var videos = await settings.GetAsync(SettingKeys.Videos, ct);
        if (
            videos.Copies == VideoCopies.Never
            || node.CurrentVersion is not { } version
            || !await media.RequestCopyAsync(version.BlobHash, JobPriority.Interactive, ct)
        )
        {
            return TypedResults.Problem(
                "No playable copy can be made of this file.",
                statusCode: StatusCodes.Status409Conflict
            );
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await mapper.MapAsync(node, ct));
    }

    private static async Task<Ok<List<VersionDto>>> ListVersions(
        Guid id,
        HttpContext context,
        DriveService drive,
        DokumentDbContext db,
        SettingsStore settings,
        TimeProvider time,
        CancellationToken ct
    )
    {
        var actor = context.Actor();
        var node = await drive.GetFileAsync(actor, id, ct);
        var versions = await drive.ListVersionsAsync(actor, id, ct);
        return TypedResults.Ok(await VersionDtosAsync(db, settings, node, versions, time, ct));
    }

    private static async Task<Ok<VersionDto>> RestoreVersion(
        Guid id,
        Guid versionId,
        HttpContext context,
        DriveService drive,
        DokumentDbContext db,
        SettingsStore settings,
        TimeProvider time,
        CancellationToken ct
    )
    {
        var actor = context.Actor();
        var restored = await drive.RestoreVersionAsync(actor, id, versionId, ct);
        var node = await drive.GetFileAsync(actor, id, ct);
        var versions = await drive.ListVersionsAsync(actor, id, ct);
        return TypedResults.Ok(
            (await VersionDtosAsync(db, settings, node, versions, time, ct)).Single(v =>
                v.Id == restored.Id
            )
        );
    }

    private static async Task<Ok<VersionDto>> SetKeepForever(
        Guid id,
        Guid versionId,
        KeepForeverRequest request,
        HttpContext context,
        DriveService drive,
        DokumentDbContext db,
        SettingsStore settings,
        TimeProvider time,
        CancellationToken ct
    )
    {
        var actor = context.Actor();
        await drive.SetKeepForeverAsync(actor, id, versionId, request.KeepForever, ct);
        var node = await drive.GetFileAsync(actor, id, ct);
        var versions = await drive.ListVersionsAsync(actor, id, ct);
        return TypedResults.Ok(
            (await VersionDtosAsync(db, settings, node, versions, time, ct)).Single(v =>
                v.Id == versionId
            )
        );
    }

    private static async Task<
        Results<Ok<DuplicateCheckResponse>, ProblemHttpResult>
    > CheckDuplicates(
        DuplicateCheckRequest request,
        HttpContext context,
        DriveService drive,
        CancellationToken ct
    )
    {
        if (request.Files.Count > 10_000)
        {
            return TypedResults.Problem(
                "Check at most 10 000 files at a time.",
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        var hashes = new List<ContentHash>();
        foreach (var file in request.Files)
        {
            if (!ContentHash.TryParse(file.Sha256, out var hash))
            {
                return TypedResults.Problem(
                    $"\"{file.Sha256}\" isn't a SHA-256 in hex.",
                    statusCode: StatusCodes.Status400BadRequest
                );
            }

            hashes.Add(hash);
        }

        var found = await drive.FindByContentAsync(context.Actor(), hashes, ct);
        var matches = await MatchesAsync(
            drive,
            found.Values.SelectMany(n => n).ToList(),
            context.Actor(),
            ct
        );
        return TypedResults.Ok(
            new DuplicateCheckResponse(
                hashes
                    .Select(h => new DuplicateCheckResult(
                        h.Hex,
                        found[h].Select(n => matches[n.Id]).ToList()
                    ))
                    .ToList()
            )
        );
    }

    private static async Task<Results<Created<NodeDto>, ProblemHttpResult>> CopyFromContent(
        CopyFromContentRequest request,
        HttpContext context,
        DriveService drive,
        NodeDtoMapper mapper,
        CancellationToken ct
    )
    {
        if (!ContentHash.TryParse(request.Sha256, out var hash))
        {
            return TypedResults.Problem(
                $"\"{request.Sha256}\" isn't a SHA-256 in hex.",
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        var added = await drive.CopyFromContentAsync(
            context.Actor(),
            request.FolderId,
            request.Name,
            hash,
            request.ClientModifiedAt,
            request.OnConflict,
            ct
        );
        return TypedResults.Created(
            $"/api/v1/nodes/{added.Node.Id}",
            await mapper.MapAsync(added.Node, ct)
        );
    }

    private static async Task<Ok<StorageUsage>> GetStorage(
        ClaimsPrincipal principal,
        DokumentDbContext db,
        SettingsStore settings,
        TrashService trash,
        CancellationToken ct
    )
    {
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == principal.UserId(), ct);
        var defaultQuota = (await settings.GetAsync(SettingKeys.Storage, ct)).DefaultQuota;
        return TypedResults.Ok(
            new StorageUsage(
                user.UsedBytes,
                user.Quota.Resolve(defaultQuota),
                await trash.FreeableBytesAsync(user.Id, ct)
            )
        );
    }

    internal static async Task<Dictionary<Guid, DuplicateMatch>> MatchesAsync(
        DriveService drive,
        IReadOnlyCollection<Node> nodes,
        Actor viewer,
        CancellationToken ct
    )
    {
        var ancestors = await drive.AncestorsAsync(nodes, ct, viewer);
        return nodes
            .DistinctBy(n => n.Id)
            .ToDictionary(
                n => n.Id,
                n => new DuplicateMatch(
                    n.Id,
                    n.Name.Value,
                    ancestors[n.Id].Select(a => new PathEntry(a.Id, a.Name.Value)).ToList(),
                    n.IsTrashed
                )
            );
    }

    private static async Task<List<PathEntry>> PathOfAsync(
        DriveService drive,
        Node node,
        Actor viewer,
        bool includeSelf,
        CancellationToken ct
    )
    {
        var path = (await drive.AncestorsAsync([node], ct, viewer))[node.Id]
            .Select(a => new PathEntry(a.Id, a.Name.Value))
            .ToList();
        if (includeSelf)
        {
            path.Add(new PathEntry(node.Id, node.Name.Value));
        }

        return path;
    }

    private static async Task<List<VersionDto>> VersionDtosAsync(
        DokumentDbContext db,
        SettingsStore settings,
        Node node,
        List<FileVersion> versions,
        TimeProvider time,
        CancellationToken ct
    )
    {
        var uploaderIds = versions.Select(v => v.UploadedBy).Distinct().ToList();
        var names = await db
            .Users.Where(u => uploaderIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var retention = (await settings.GetAsync(SettingKeys.Versions, ct)).Retention;
        var purge = retention.PurgeDates(versions, node.CurrentVersionId, time.GetUtcNow());
        return versions
            .Select(v => new VersionDto(
                v.Id,
                v.Number,
                v.Size,
                v.MimeType,
                v.OriginalName.Value,
                v.UploadedBy,
                names.GetValueOrDefault(v.UploadedBy, ""),
                v.UploadedAt,
                v.ClientModifiedAt,
                v.KeepForever,
                v.Id == node.CurrentVersionId,
                v.RestoredFrom,
                purge[v.Number]
            ))
            .ToList();
    }
}
