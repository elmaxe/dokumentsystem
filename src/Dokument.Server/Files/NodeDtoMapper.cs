using Dokument.Domain.Files;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Jobs;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Previews;
using Dokument.Infrastructure.Tags;
using Dokument.Server.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Dokument.Server.Files;

/// <summary>
/// Turns nodes into <see cref="NodeDto"/>s, looking up owner names, thumbnail states, unfinished processing steps,
/// folder sizes, tags and the signed-in person's stars for the whole batch at once.
/// </summary>
public sealed class NodeDtoMapper(
    DokumentDbContext db,
    PreviewService previews,
    JobMonitor jobs,
    DriveService drive,
    StarService stars,
    TagService tags,
    IHttpContextAccessor http
)
{
    public async Task<List<NodeDto>> MapAsync(IReadOnlyCollection<Node> nodes, CancellationToken ct)
    {
        var ownerIds = nodes.Select(n => n.OwnerId).Distinct().ToList();
        var owners = await db
            .Users.Where(u => ownerIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var hashes = nodes.Select(n => n.CurrentVersion?.BlobHash).OfType<ContentHash>().ToList();
        var thumbnails = await previews.ThumbnailStatesAsync(hashes, ct);
        var processing = await jobs.ActiveAsync(hashes, ct);
        var folders = await drive.FolderContentsAsync(nodes, ct);
        var starred =
            http.HttpContext?.User.Identity?.IsAuthenticated == true
                ? await stars.StarredAsync(
                    http.HttpContext.User.UserId(),
                    nodes.Select(n => n.Id).ToList(),
                    ct
                )
                : [];
        var tagged = await tags.ForNodesAsync(nodes.Select(n => n.Id).ToList(), ct);
        return nodes
            .Select(n => NodeDto.From(n, owners, thumbnails, folders, starred, processing, tagged))
            .ToList();
    }

    public async Task<NodeDto> MapAsync(Node node, CancellationToken ct) =>
        (await MapAsync([node], ct))[0];
}
