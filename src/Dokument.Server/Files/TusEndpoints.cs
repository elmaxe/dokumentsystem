using System.Globalization;
using System.Text;
using Dokument.Application.Files;
using Dokument.Domain.Files;
using Dokument.Infrastructure.Files;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Dokument.Server.Files;

/// <summary>
/// Resumable uploads over the tus 1.0 protocol (https://tus.io/protocols/resumable-upload), core plus the creation,
/// termination and expiration extensions (docs/design.md §8.1). Any tus client works; the file's details travel in
/// <c>Upload-Metadata</c>: <c>filename</c>, and optionally <c>folderId</c>, <c>nodeId</c> (new version of that file),
/// <c>sha256</c>, <c>filetype</c>, <c>modifiedAt</c> (ms since the epoch) and <c>onConflict</c> (fail, keepBoth, newVersion).
/// </summary>
public static class TusEndpoints
{
    public const string Version = "1.0.0";
    public const string OffsetContentType = "application/offset+octet-stream";

    /// <summary>Set on the response that completes an upload: the file it created or added a version to.</summary>
    public const string NodeIdHeader = "Dokument-Node-Id";

    public static RouteGroupBuilder MapTusEndpoints(this RouteGroupBuilder api)
    {
        var uploads = api.MapGroup("/uploads").WithTags("Uploads").RequireAuthorization();
        uploads
            .MapMethods("", [HttpMethods.Options], Options)
            .WithName("TusOptions")
            .ExcludeFromDescription();
        uploads.MapPost("", Create).WithName("CreateUpload").ExcludeFromDescription();
        uploads
            .MapMethods("/{id:guid}", [HttpMethods.Head], Head)
            .WithName("TusHead")
            .ExcludeFromDescription();
        uploads.MapPatch("/{id:guid}", Append).WithName("TusPatch").ExcludeFromDescription();
        uploads
            .MapDelete("/{id:guid}", Terminate)
            .WithName("TusTerminate")
            .ExcludeFromDescription();
        uploads
            .MapGet("/{id:guid}", GetStatus)
            .WithName("GetUpload")
            .WithDescription(
                "The state of a tus upload, and once complete, the file it produced and any duplicates of it."
            );
        return api;
    }

    private static NoContent Options(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["Tus-Resumable"] = Version;
        headers["Tus-Version"] = Version;
        headers["Tus-Extension"] = "creation,creation-with-upload,termination,expiration";
        return TypedResults.NoContent();
    }

    private static async Task<IResult> Create(
        HttpContext context,
        UploadService uploads,
        CancellationToken ct
    )
    {
        if (CheckVersion(context) is { } wrongVersion)
        {
            return wrongVersion;
        }

        if (
            !long.TryParse(
                context.Request.Headers["Upload-Length"],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var length
            )
        )
        {
            return Problem(
                context,
                StatusCodes.Status400BadRequest,
                "Upload-Length is required. Deferred lengths aren't supported."
            );
        }

        if (
            !TryParseMetadata(
                context.Request.Headers["Upload-Metadata"],
                out var metadata,
                out var error
            ) || !TryReadRequest(metadata, length, out var request, out error)
        )
        {
            return Problem(context, StatusCodes.Status400BadRequest, error);
        }

        var actor = context.Actor();
        var upload = await uploads.CreateAsync(actor, request, ct);

        // creation-with-upload: the first chunk may come with the create request.
        if (
            !upload.IsComplete
            && context.Request.ContentType == OffsetContentType
            && context.Request.ContentLength is > 0
        )
        {
            AllowLargeBody(context);
            upload = await uploads.AppendAsync(actor, upload.Id, 0, context.Request.Body, ct);
        }

        SetUploadHeaders(context, upload);
        context.Response.Headers.Location =
            $"{context.Request.PathBase}/api/v1/uploads/{upload.Id}";
        return TypedResults.StatusCode(StatusCodes.Status201Created);
    }

    private static async Task<IResult> Head(
        Guid id,
        HttpContext context,
        UploadService uploads,
        CancellationToken ct
    )
    {
        if (CheckVersion(context) is { } wrongVersion)
        {
            return wrongVersion;
        }

        var upload = await uploads.GetAsync(context.Actor(), id, ct);
        SetUploadHeaders(context, upload);
        context.Response.Headers["Upload-Length"] = upload.Size.ToString(
            CultureInfo.InvariantCulture
        );
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok();
    }

    private static async Task<IResult> Append(
        Guid id,
        HttpContext context,
        UploadService uploads,
        CancellationToken ct
    )
    {
        if (CheckVersion(context) is { } wrongVersion)
        {
            return wrongVersion;
        }

        if (context.Request.ContentType != OffsetContentType)
        {
            return Problem(
                context,
                StatusCodes.Status415UnsupportedMediaType,
                $"Send chunks as {OffsetContentType}."
            );
        }

        if (
            !long.TryParse(
                context.Request.Headers["Upload-Offset"],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var offset
            )
        )
        {
            return Problem(context, StatusCodes.Status400BadRequest, "Upload-Offset is required.");
        }

        AllowLargeBody(context);
        var upload = await uploads.AppendAsync(
            context.Actor(),
            id,
            offset,
            context.Request.Body,
            ct
        );
        SetUploadHeaders(context, upload);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> Terminate(
        Guid id,
        HttpContext context,
        UploadService uploads,
        CancellationToken ct
    )
    {
        if (CheckVersion(context) is { } wrongVersion)
        {
            return wrongVersion;
        }

        await uploads.TerminateAsync(context.Actor(), id, ct);
        context.Response.Headers["Tus-Resumable"] = Version;
        return TypedResults.NoContent();
    }

    private static async Task<Ok<UploadStatus>> GetStatus(
        Guid id,
        HttpContext context,
        UploadService uploads,
        DriveService drive,
        CancellationToken ct
    )
    {
        var actor = context.Actor();
        var upload = await uploads.GetAsync(actor, id, ct);
        List<DuplicateMatch> duplicates = [];
        if (upload.ResultNodeId is { } nodeId)
        {
            var node = await drive.GetAsync(actor, nodeId, ct);
            if (node.CurrentVersion is { } version)
            {
                var others = (await drive.FindByContentAsync(actor, [version.BlobHash], ct))[
                    version.BlobHash
                ]
                    .Where(n => n.Id != nodeId)
                    .ToList();
                var matches = await FileEndpoints.MatchesAsync(drive, others, ct);
                duplicates = others.Select(n => matches[n.Id]).ToList();
            }
        }

        return TypedResults.Ok(
            new UploadStatus(
                upload.Id,
                upload.Name.Value,
                upload.Size,
                upload.Offset,
                upload.IsComplete,
                upload.ResultNodeId,
                upload.ExpiresAt,
                duplicates
            )
        );
    }

    private static void SetUploadHeaders(HttpContext context, Upload upload)
    {
        var headers = context.Response.Headers;
        headers["Tus-Resumable"] = Version;
        headers["Upload-Offset"] = upload.Offset.ToString(CultureInfo.InvariantCulture);
        headers["Upload-Expires"] = upload.ExpiresAt.ToString("R", CultureInfo.InvariantCulture);
        if (upload.ResultNodeId is { } nodeId)
        {
            headers[NodeIdHeader] = nodeId.ToString();
        }
    }

    /// <summary>Chunks can be far larger than Kestrel's default 30 MB body limit; the upload's declared length bounds them instead.</summary>
    private static void AllowLargeBody(HttpContext context)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = null;
        }
    }

    private static IResult? CheckVersion(HttpContext context)
    {
        if (context.Request.Headers["Tus-Resumable"] == Version)
        {
            return null;
        }

        context.Response.Headers["Tus-Version"] = Version;
        return Problem(
            context,
            StatusCodes.Status412PreconditionFailed,
            $"Send Tus-Resumable: {Version}."
        );
    }

    private static ProblemHttpResult Problem(HttpContext context, int status, string detail)
    {
        context.Response.Headers["Tus-Resumable"] = Version;
        return TypedResults.Problem(detail: detail, statusCode: status);
    }

    /// <summary>Parses <c>key base64value,key2 base64value2</c>. Keys without a value are allowed and read as empty.</summary>
    public static bool TryParseMetadata(
        string? header,
        out Dictionary<string, string> metadata,
        out string error
    )
    {
        metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        error = "";
        if (string.IsNullOrWhiteSpace(header))
        {
            return true;
        }

        foreach (
            var pair in header.Split(
                ',',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries
            )
        )
        {
            var parts = pair.Split(' ', 2, StringSplitOptions.TrimEntries);
            try
            {
                metadata[parts[0]] =
                    parts.Length > 1
                        ? Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]))
                        : "";
            }
            catch (FormatException)
            {
                error = $"Upload-Metadata \"{parts[0]}\" isn't valid base64.";
                return false;
            }
        }

        return true;
    }

    public static bool TryReadRequest(
        Dictionary<string, string> metadata,
        long length,
        out NewUpload request,
        out string error
    )
    {
        request = null!;
        error = "";
        Guid? folderId = null,
            nodeId = null;
        ContentHash? sha256 = null;
        DateTimeOffset? modifiedAt = null;
        var onConflict = NameConflictPolicy.Fail;

        if (metadata.GetValueOrDefault("folderId") is { Length: > 0 } folder)
        {
            if (!Guid.TryParse(folder, out var parsed))
            {
                error = "folderId isn't a valid id.";
                return false;
            }

            folderId = parsed;
        }

        if (metadata.GetValueOrDefault("nodeId") is { Length: > 0 } node)
        {
            if (!Guid.TryParse(node, out var parsed))
            {
                error = "nodeId isn't a valid id.";
                return false;
            }

            nodeId = parsed;
        }

        if (metadata.GetValueOrDefault("sha256") is { Length: > 0 } hash)
        {
            if (!ContentHash.TryParse(hash, out sha256))
            {
                error = "sha256 isn't a SHA-256 in hex.";
                return false;
            }
        }

        if (metadata.GetValueOrDefault("modifiedAt") is { Length: > 0 } modified)
        {
            if (
                !long.TryParse(
                    modified,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var ms
                )
            )
            {
                error = "modifiedAt must be milliseconds since the epoch.";
                return false;
            }

            modifiedAt = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }

        if (
            metadata.GetValueOrDefault("onConflict") is { Length: > 0 } conflict
            && !Enum.TryParse(conflict, ignoreCase: true, out onConflict)
        )
        {
            error = "onConflict must be fail, keepBoth or newVersion.";
            return false;
        }

        request = new NewUpload(
            metadata.GetValueOrDefault("filename") ?? metadata.GetValueOrDefault("name"),
            length,
            folderId,
            nodeId,
            sha256,
            metadata.GetValueOrDefault("filetype"),
            modifiedAt,
            onConflict
        );
        return true;
    }
}
