using Dokument.Application.Files;
using Dokument.Domain.Files;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Live;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Dokument.Server.Files;

/// <summary>Stars, the Starred page and the Recent page (docs/design.md §15.2).</summary>
public static class StarredAndRecentEndpoints
{
    public static RouteGroupBuilder MapStarredAndRecentEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("").WithTags("Starred and recent").RequireAuthorization();
        group
            .MapPost("/nodes/star", Star)
            .WithName("StarNodes")
            .WithDescription(
                "Stars files and folders for the person asking. Stars are each person's own."
            );
        group.MapPost("/nodes/unstar", Unstar).WithName("UnstarNodes");
        group
            .MapGet("/starred", ListStarred)
            .WithName("ListStarred")
            .WithDescription(
                "What the person starred, trashed items left out. Unsorted: clients sort."
            );
        group
            .MapPost("/nodes/{id:guid}/opened", Opened)
            .WithName("MarkOpened")
            .WithDescription(
                "The viewer calls this when the person opens a file, for their Recent page. Private to them and not audited (§18)."
            );
        group
            .MapGet("/recent", ListRecent)
            .WithName("ListRecent")
            .WithDescription(
                "The files the person last opened, downloaded, uploaded or restored a version of, newest first, at most 100."
            );
        return api;
    }

    private static async Task<NoContent> Star(
        NodeIdsRequest request,
        HttpContext context,
        StarService stars,
        CancellationToken ct
    )
    {
        await stars.SetAsync(context.Actor(), request.Ids, starred: true, ct);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> Unstar(
        NodeIdsRequest request,
        HttpContext context,
        StarService stars,
        CancellationToken ct
    )
    {
        await stars.SetAsync(context.Actor(), request.Ids, starred: false, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<StarredListing>> ListStarred(
        HttpContext context,
        StarService stars,
        DriveService drive,
        NodeDtoMapper mapper,
        CancellationToken ct
    )
    {
        var nodes = await stars.ListAsync(context.Actor(), ct);
        var dtos = await mapper.MapAsync(nodes, ct);
        var paths = await PathsAsync(drive, nodes, context.Actor(), ct);
        return TypedResults.Ok(
            new StarredListing(
                nodes.Select((n, i) => new StarredItem(dtos[i], paths[n.Id])).ToList()
            )
        );
    }

    private static async Task<NoContent> Opened(
        Guid id,
        HttpContext context,
        DriveService drive,
        RecentFiles recent,
        LiveChanges live,
        CancellationToken ct
    )
    {
        var actor = context.Actor();
        var node = await drive.GetFileAsync(actor, id, ct);
        await recent.RecordAsync(actor.UserId, node.Id, RecentAction.Opened, ct);
        await live.NotifyAsync(actor.UserId, LiveTopics.Recent, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<RecentListing>> ListRecent(
        HttpContext context,
        RecentFiles recent,
        DriveService drive,
        NodeDtoMapper mapper,
        CancellationToken ct
    )
    {
        var entries = await recent.ListAsync(context.Actor(), ct);
        var nodes = entries.Select(e => e.Node).ToList();
        var dtos = await mapper.MapAsync(nodes, ct);
        var paths = await PathsAsync(drive, nodes, context.Actor(), ct);
        return TypedResults.Ok(
            new RecentListing(
                entries
                    .Select((e, i) => new RecentItem(dtos[i], paths[e.Node.Id], e.At, e.Action))
                    .ToList()
            )
        );
    }

    private static async Task<Dictionary<Guid, List<PathEntry>>> PathsAsync(
        DriveService drive,
        List<Node> nodes,
        Actor viewer,
        CancellationToken ct
    ) =>
        (await drive.AncestorsAsync(nodes, ct, viewer)).ToDictionary(
            a => a.Key,
            a => a.Value.Select(f => new PathEntry(f.Id, f.Name.Value)).ToList()
        );
}
