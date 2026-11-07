using System.Threading.RateLimiting;
using Dokument.Domain.Files;
using Dokument.Domain.Previews;
using Dokument.Domain.Sharing;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Previews;
using Dokument.Infrastructure.Sharing;
using Dokument.Infrastructure.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;

namespace Dokument.Server.Files;

/// <summary>
/// Public links (docs/design.md §9): the owner's settings under <c>/nodes/{id}/link</c>, and what anyone with a link
/// sees under <c>/links/{token}</c>, without signing in. The public side is rate-limited per address.
/// </summary>
public static class LinkEndpoints
{
    public const string RateLimitPolicy = "links";
    public const string UnlockRateLimitPolicy = "link-unlock";

    /// <summary>The cookie a link's password sets, scoped to that link's API path.</summary>
    public const string UnlockCookie = "dk_link";

    /// <summary>Enough for a folder of thumbnails and a video seeking; a token can't be guessed at this rate.</summary>
    public static void AddLinkRateLimits(this RateLimiterOptions options)
    {
        options.AddPolicy(
            RateLimitPolicy,
            context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 1200,
                        Window = TimeSpan.FromMinutes(1),
                    }
                )
        );
        options.AddPolicy(
            UnlockRateLimitPolicy,
            context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(1),
                    }
                )
        );
    }

    public static RouteGroupBuilder MapLinkEndpoints(this RouteGroupBuilder api)
    {
        var owner = api.MapGroup("/nodes/{id:guid}/link").WithTags("Links").RequireAuthorization();
        owner
            .MapGet("", Get)
            .WithName("GetLink")
            .WithDescription("Owner only. The node's public link, or 204 when it has none.");
        owner
            .MapPut("", Save)
            .WithName("SaveLink")
            .WithDescription(
                "Owner only. Turns the node's public link on, or changes its expiry, password or download setting."
            );
        owner
            .MapDelete("", Revoke)
            .WithName("RevokeLink")
            .WithDescription("Owner only. Turns the public link off; it stops working at once.");

        var links = api.MapGroup("/links/{token}")
            .WithTags("Public links")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicy);
        links
            .MapGet("", Open)
            .WithName("OpenLink")
            .WithDescription(
                "What a public link shows, without signing in. Audited as an access. 404 linkGone when it was turned off, has expired or its file is gone; 401 passwordRequired until the password is given."
            );
        links
            .MapPost("/unlock", Unlock)
            .WithName("UnlockLink")
            .WithDescription(
                "Checks the link's password and sets a cookie that lets this browser in for 12 hours."
            )
            .RequireRateLimiting(UnlockRateLimitPolicy);
        links
            .MapGet("/nodes", List)
            .WithName("ListLinkFolder")
            .WithDescription(
                "A folder behind a public link: the link's own folder, or one inside it."
            );
        links.MapGet("/nodes/{nodeId:guid}", GetNode).WithName("GetLinkNode");
        links.MapGet("/nodes/{nodeId:guid}/thumbnail", Thumbnail).WithName("GetLinkThumbnail");
        links.MapGet("/nodes/{nodeId:guid}/preview", Preview).WithName("GetLinkPreview");
        links.MapGet("/nodes/{nodeId:guid}/waveform", Waveform).WithName("GetLinkWaveform");
        links
            .MapGet("/nodes/{nodeId:guid}/seek-previews", SeekPreviews)
            .WithName("GetLinkSeekPreviews");
        links
            .MapGet("/nodes/{nodeId:guid}/content", Download)
            .WithName("DownloadLinkFile")
            .WithDescription(
                "The current version's bytes, when the link allows downloads. Audited as a download through the link."
            );
        return api;
    }

    private static async Task<Results<Ok<LinkDto>, NoContent>> Get(
        Guid id,
        HttpContext context,
        LinkService links,
        CancellationToken ct
    ) =>
        await links.GetAsync(context.Actor(), id, ct) is { } link
            ? TypedResults.Ok(LinkDto.From(link, context))
            : TypedResults.NoContent();

    private static async Task<Results<Ok<LinkDto>, ValidationProblem>> Save(
        Guid id,
        SaveLinkRequest request,
        HttpContext context,
        LinkService links,
        CancellationToken ct
    )
    {
        PasswordChange password;
        if (request.RemovePassword)
        {
            password = new PasswordChange.Remove();
        }
        else if (request.Password is null)
        {
            password = new PasswordChange.Keep();
        }
        else if (LinkPassword.TryCreate(request.Password, out var typed, out var error))
        {
            password = new PasswordChange.Set(typed);
        }
        else
        {
            return TypedResults.ValidationProblem(
                new Dictionary<string, string[]> { ["password"] = [error] }
            );
        }

        var link = await links.SaveAsync(
            context.Actor(),
            id,
            new LinkSettings(request.AllowDownload, request.ExpiresAt, password),
            ct
        );
        return TypedResults.Ok(LinkDto.From(link, context));
    }

    private static async Task<NoContent> Revoke(
        Guid id,
        HttpContext context,
        LinkService links,
        CancellationToken ct
    )
    {
        await links.RevokeAsync(context.Actor(), id, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<LinkPage>> Open(
        string token,
        HttpContext context,
        LinkService links,
        NodeDtoMapper mapper,
        CancellationToken ct
    )
    {
        var link = await OpenAsync(token, context, links, ct);
        var (ip, userAgent) = Client(context);
        await links.RecordAccessAsync(link, ip, userAgent, ct);
        return TypedResults.Ok(
            new LinkPage(
                LinkNodeDto.From(await mapper.MapAsync(link.Root, ct)),
                link.Owner.DisplayName,
                link.Link.ExpiresAt,
                link.Link.AllowDownload
            )
        );
    }

    private static async Task<NoContent> Unlock(
        string token,
        UnlockLinkRequest request,
        HttpContext context,
        LinkService links,
        CancellationToken ct
    )
    {
        var cookie = await links.UnlockAsync(token, request.Password, ct);
        context.Response.Cookies.Append(
            UnlockCookie,
            cookie,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = context.Request.IsHttps,
                SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax,
                Path = CookiePath(token),
                MaxAge = LinkService.UnlockLifetime,
            }
        );
        return TypedResults.NoContent();
    }

    private static async Task<Ok<LinkFolder>> List(
        string token,
        Guid? folderId,
        HttpContext context,
        LinkService links,
        NodeDtoMapper mapper,
        CancellationToken ct
    )
    {
        var link = await OpenAsync(token, context, links, ct);
        var folder = await links.NodeAsync(link, folderId, ct);
        var items = await links.ChildrenAsync(link, folder, ct);
        var dtos = await mapper.MapAsync([.. items, folder], ct);
        var path = await links.PathAsync(link, folder, ct);
        return TypedResults.Ok(
            new LinkFolder(
                LinkNodeDto.From(dtos[^1]),
                [
                    .. path.Select(p => new PathEntry(p.Id, p.Name.Value)),
                    new(folder.Id, folder.Name.Value),
                ],
                dtos[..items.Count].Select(LinkNodeDto.From).ToList()
            )
        );
    }

    private static async Task<Ok<LinkNodeDetails>> GetNode(
        string token,
        Guid nodeId,
        HttpContext context,
        LinkService links,
        NodeDtoMapper mapper,
        CancellationToken ct
    )
    {
        var link = await OpenAsync(token, context, links, ct);
        var node = await links.NodeAsync(link, nodeId, ct);
        var path = await links.PathAsync(link, node, ct);
        return TypedResults.Ok(
            new LinkNodeDetails(
                LinkNodeDto.From(await mapper.MapAsync(node, ct)),
                path.Select(p => new PathEntry(p.Id, p.Name.Value)).ToList()
            )
        );
    }

    /// <param name="size"><c>small</c> (256 px) or <c>medium</c> (640 px, the default).</param>
    private static async Task<
        Results<PhysicalFileHttpResult, NotFound, ProblemHttpResult>
    > Thumbnail(
        string token,
        Guid nodeId,
        string? size,
        HttpContext context,
        LinkService links,
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

        return await RenditionAsync(token, nodeId, kind, context, links, previews, ct) is { } file
            ? file
            : TypedResults.NotFound();
    }

    /// <param name="original">For video and audio: the original even when a playable copy exists.</param>
    private static async Task<PhysicalFileHttpResult> Preview(
        string token,
        Guid nodeId,
        bool? original,
        HttpContext context,
        LinkService links,
        PreviewService previews,
        CancellationToken ct
    )
    {
        var link = await OpenAsync(token, context, links, ct);
        var content = await links.ContentAsync(link, nodeId, ct);
        return await FileEndpoints.PreviewOf(context, content, original, previews, ct);
    }

    private static async Task<Results<PhysicalFileHttpResult, NotFound>> Waveform(
        string token,
        Guid nodeId,
        HttpContext context,
        LinkService links,
        PreviewService previews,
        CancellationToken ct
    ) =>
        await RenditionAsync(token, nodeId, RenditionKind.Waveform, context, links, previews, ct)
            is { } file
            ? file
            : TypedResults.NotFound();

    private static async Task<Results<PhysicalFileHttpResult, NotFound>> SeekPreviews(
        string token,
        Guid nodeId,
        HttpContext context,
        LinkService links,
        PreviewService previews,
        CancellationToken ct
    ) =>
        await RenditionAsync(
            token,
            nodeId,
            RenditionKind.SeekPreviews,
            context,
            links,
            previews,
            ct
        )
            is { } file
            ? file
            : TypedResults.NotFound();

    private static async Task<PhysicalFileHttpResult> Download(
        string token,
        Guid nodeId,
        HttpContext context,
        LinkService links,
        CancellationToken ct
    )
    {
        var link = await OpenAsync(token, context, links, ct);

        // Only the request that starts at the first byte is audited, as for people signed in.
        var range = context.Request.Headers.Range.ToString();
        var audited = range.Length == 0 || range.StartsWith("bytes=0-", StringComparison.Ordinal);
        var (ip, userAgent) = Client(context);
        var content = await links.DownloadAsync(link, nodeId, audited, ip, userAgent, ct);
        if (audited)
        {
            DokumentTelemetry.Downloads.Add(1);
        }

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

    private static async Task<PhysicalFileHttpResult?> RenditionAsync(
        string token,
        Guid nodeId,
        RenditionKind kind,
        HttpContext context,
        LinkService links,
        PreviewService previews,
        CancellationToken ct
    )
    {
        var link = await OpenAsync(token, context, links, ct);
        var content = await links.ContentAsync(link, nodeId, ct);
        return
            await previews.FindAsync(content.Version.BlobHash, kind, ct) is { } file
            && File.Exists(file.PhysicalPath)
            ? FileEndpoints.Rendition(context, file)
            : null;
    }

    private static Task<OpenLink> OpenAsync(
        string token,
        HttpContext context,
        LinkService links,
        CancellationToken ct
    ) => links.OpenAsync(token, context.Request.Cookies[UnlockCookie], ct);

    private static (string? Ip, string? UserAgent) Client(HttpContext context) =>
        (
            context.Connection.RemoteIpAddress?.ToString(),
            context.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null
        );

    private static string CookiePath(string token) => $"/api/v1/links/{token}";
}
