using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Dokument.Server.Files;

/// <summary>Moving to the trash, the Trash page, restore and permanent deletion (docs/design.md §12).</summary>
public static class TrashEndpoints
{
    public static RouteGroupBuilder MapTrashEndpoints(this RouteGroupBuilder api)
    {
        var trash = api.MapGroup("").WithTags("Trash").RequireAuthorization();
        trash
            .MapPost("/nodes/trash", Trash)
            .WithName("TrashNodes")
            .WithDescription(
                "Moves files and folders to the trash, folders with everything in them. Undo with /trash/restore."
            );
        trash
            .MapGet("/trash", List)
            .WithName("ListTrash")
            .WithDescription(
                "What the user trashed, newest first. Things that went with a folder are counted in it, not listed."
            );
        trash.MapPost("/trash/restore", Restore).WithName("RestoreFromTrash");
        trash
            .MapPost("/trash/delete", DeleteForever)
            .WithName("DeleteForever")
            .WithDescription("Deletes items in the trash forever. Can't be undone.");
        trash
            .MapPost("/trash/empty", Empty)
            .WithName("EmptyTrash")
            .WithDescription("Deletes everything in the trash forever. Can't be undone.");
        return api;
    }

    private static async Task<NoContent> Trash(
        NodeIdsRequest request,
        HttpContext context,
        TrashService trash,
        CancellationToken ct
    )
    {
        await trash.TrashAsync(context.Actor(), request.Ids, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<TrashListing>> List(
        HttpContext context,
        TrashService trash,
        DriveService drive,
        NodeDtoMapper mapper,
        SettingsStore settings,
        DokumentDbContext db,
        CancellationToken ct
    )
    {
        var actor = context.Actor();
        var entries = await trash.ListAsync(actor, ct);
        var nodes = entries.Select(e => e.Node).ToList();
        var dtos = await mapper.MapAsync(nodes, ct);
        var ancestors = await drive.AncestorsAsync(nodes, ct);
        var trashers = nodes
            .Select(n => n.TrashedBy)
            .OfType<Guid>()
            .Where(id => id != actor.UserId)
            .Distinct()
            .ToList();
        var trasherNames = await db
            .Users.Where(u => trashers.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return TypedResults.Ok(
            new TrashListing(
                entries
                    .Select(
                        (e, i) =>
                            new TrashItem(
                                dtos[i],
                                ancestors[e.Node.Id]
                                    .Select(a => new PathEntry(a.Id, a.Name.Value))
                                    .ToList(),
                                e.Node.TrashedAt!.Value,
                                e.DeleteAt,
                                e.ItemCount,
                                e.Bytes,
                                e.Node.TrashedBy is { } by
                                    ? trasherNames.GetValueOrDefault(by)
                                    : null
                            )
                    )
                    .ToList(),
                await trash.FreeableBytesAsync(actor.UserId, ct),
                (await settings.GetAsync(SettingKeys.Trash, ct)).KeepDays
            )
        );
    }

    private static async Task<Ok<RestoreResponse>> Restore(
        NodeIdsRequest request,
        HttpContext context,
        TrashService trash,
        NodeDtoMapper mapper,
        CancellationToken ct
    )
    {
        var restored = await trash.RestoreAsync(context.Actor(), request.Ids, ct);
        var dtos = await mapper.MapAsync(restored.Select(r => r.Node).ToList(), ct);
        return TypedResults.Ok(
            new RestoreResponse(
                restored.Select((r, i) => new RestoredItem(dtos[i], r.MovedToRoot)).ToList()
            )
        );
    }

    private static async Task<NoContent> DeleteForever(
        NodeIdsRequest request,
        HttpContext context,
        TrashService trash,
        CancellationToken ct
    )
    {
        await trash.DeleteForeverAsync(context.Actor(), request.Ids, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<EmptyTrashResponse>> Empty(
        HttpContext context,
        TrashService trash,
        CancellationToken ct
    ) => TypedResults.Ok(new EmptyTrashResponse(await trash.EmptyAsync(context.Actor(), ct)));
}
