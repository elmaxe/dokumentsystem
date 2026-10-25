using Dokument.Domain.Files;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Previews;
using Microsoft.EntityFrameworkCore;

namespace Dokument.Server.Files;

/// <summary>Turns nodes into <see cref="NodeDto"/>s, looking up owner names, thumbnail states and folder sizes for the whole batch at once.</summary>
public sealed class NodeDtoMapper(DokumentDbContext db, PreviewService previews, DriveService drive)
{
    public async Task<List<NodeDto>> MapAsync(IReadOnlyCollection<Node> nodes, CancellationToken ct)
    {
        var ownerIds = nodes.Select(n => n.OwnerId).Distinct().ToList();
        var owners = await db
            .Users.Where(u => ownerIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var thumbnails = await previews.ThumbnailStatesAsync(
            nodes.Select(n => n.CurrentVersion?.BlobHash).OfType<ContentHash>().ToList(),
            ct
        );
        var folders = await drive.FolderContentsAsync(nodes, ct);
        return nodes.Select(n => NodeDto.From(n, owners, thumbnails, folders)).ToList();
    }

    public async Task<NodeDto> MapAsync(Node node, CancellationToken ct) =>
        (await MapAsync([node], ct))[0];
}
