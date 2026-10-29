using Dokument.Application.Files;
using Dokument.Domain.Files;
using Dokument.Domain.Sharing;
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
/// media, folder sizes, tags and the signed-in person's stars for the whole batch at once.
/// </summary>
public sealed class NodeDtoMapper(
    DokumentDbContext db,
    PreviewService previews,
    JobMonitor jobs,
    MediaService mediaService,
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
        var media = await mediaService.StatesAsync(hashes, ct);
        var documents = await previews.DocumentsAsync(hashes, ct);
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
        var (roles, shared) = await AccessAsync(nodes, ct);
        return nodes
            .Select(n =>
                NodeDto.From(
                    n,
                    owners,
                    thumbnails,
                    folders,
                    starred,
                    processing,
                    tagged,
                    media,
                    documents,
                    roles,
                    shared
                )
            )
            .ToList();
    }

    /// <summary>
    /// The signed-in person's role on each node, and which of their own nodes they shared with someone directly (§9).
    /// </summary>
    private async Task<(Dictionary<Guid, NodeRole> Roles, HashSet<Guid> Shared)> AccessAsync(
        IReadOnlyCollection<Node> nodes,
        CancellationToken ct
    )
    {
        if (http.HttpContext?.User.Identity?.IsAuthenticated != true)
        {
            return ([], []);
        }

        var me = http.HttpContext.User.UserId();
        var roles = nodes.Where(n => n.OwnerId == me).ToDictionary(n => n.Id, _ => NodeRole.Owner);
        var others = nodes.Where(n => n.OwnerId != me).Select(n => n.Id).Distinct().ToList();
        if (others.Count > 0)
        {
            var shared = await db
                .Nodes.Where(n => others.Contains(n.Id))
                .Select(n => new { n.Id, Role = NodeAccess.SharedRole(me, n.Path) })
                .ToListAsync(ct);
            foreach (var row in shared)
            {
                roles[row.Id] =
                    row.Role >= (int)ShareRole.Editor ? NodeRole.Editor : NodeRole.Viewer;
            }
        }

        var owned = roles.Where(r => r.Value == NodeRole.Owner).Select(r => r.Key).ToList();
        var withShares =
            owned.Count == 0
                ? []
                : await db
                    .Shares.Where(s => owned.Contains(s.NodeId))
                    .Select(s => s.NodeId)
                    .Distinct()
                    .ToListAsync(ct);
        return (roles, [.. withShares]);
    }

    public async Task<NodeDto> MapAsync(Node node, CancellationToken ct) =>
        (await MapAsync([node], ct))[0];
}
