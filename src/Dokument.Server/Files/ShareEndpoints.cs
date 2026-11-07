using Dokument.Application.Files;
using Dokument.Domain.Identity;
using Dokument.Domain.Sharing;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Sharing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Dokument.Server.Files;

/// <summary>Sharing with people and Shared with me (docs/design.md §9).</summary>
public static class ShareEndpoints
{
    public static RouteGroupBuilder MapShareEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("").WithTags("Sharing").RequireAuthorization();
        group
            .MapGet("/people", People)
            .WithName("ListPeople")
            .WithDescription(
                "The people the signed-in person can share with: everyone who has signed in and isn't disabled, by name or email, at most 50."
            );
        group
            .MapGet("/nodes/{id:guid}/access", GetAccess)
            .WithName("GetAccess")
            .WithDescription(
                "Who can see a file or folder and why: its owner and everyone it or a folder above it is shared with."
            );
        group
            .MapPost("/nodes/{id:guid}/shares", Grant)
            .WithName("ShareNode")
            .WithDescription(
                "Owner only. Shares with a person, or changes the role they have on this node directly. The role must be more than a folder above already gives them."
            );
        group.MapPut("/shares/{id:guid}", Change).WithName("ChangeShare");
        group
            .MapDelete("/shares/{id:guid}", Remove)
            .WithName("RemoveShare")
            .WithDescription("The owner stops sharing with someone, or the person leaves.");
        group
            .MapPost("/nodes/{id:guid}/leave", Leave)
            .WithName("LeaveShare")
            .WithDescription("Ends the signed-in person's own share on this node.");
        group
            .MapPost("/nodes/{id:guid}/seen", Seen)
            .WithName("MarkShareSeen")
            .WithDescription(
                "Something shared with the person was opened, so it is no longer New."
            );
        group
            .MapGet("/shared-with-me", SharedWithMe)
            .WithName("ListSharedWithMe")
            .WithDescription(
                "The top-most things shared with the person, newest share first. Folders inside a listed one are reached from it."
            );
        group
            .MapPost("/nodes/move-check", MoveCheck)
            .WithName("CheckMove")
            .WithDescription(
                "Who would no longer see these items if they moved to the folder, so the client can ask first."
            );
        return api;
    }

    private static async Task<Ok<List<PersonDto>>> People(
        string? q,
        HttpContext context,
        ShareService shares,
        CancellationToken ct
    ) =>
        TypedResults.Ok(
            (await shares.PeopleAsync(context.Actor(), q, ct)).Select(PersonDto.From).ToList()
        );

    private static async Task<Ok<AccessDto>> GetAccess(
        Guid id,
        HttpContext context,
        ShareService shares,
        CancellationToken ct
    ) => TypedResults.Ok(AccessDto.From(await shares.AccessAsync(context.Actor(), id, ct)));

    private static async Task<Ok<AccessDto>> Grant(
        Guid id,
        ShareRequest request,
        HttpContext context,
        ShareService shares,
        CancellationToken ct
    )
    {
        var actor = context.Actor();
        await shares.GrantAsync(actor, id, request.UserId, request.Role, ct);
        return TypedResults.Ok(AccessDto.From(await shares.AccessAsync(actor, id, ct)));
    }

    private static async Task<Ok<AccessDto>> Change(
        Guid id,
        ChangeShareRequest request,
        HttpContext context,
        ShareService shares,
        CancellationToken ct
    )
    {
        var actor = context.Actor();
        var share = await shares.ChangeAsync(actor, id, request.Role, ct);
        return TypedResults.Ok(AccessDto.From(await shares.AccessAsync(actor, share.NodeId, ct)));
    }

    private static async Task<NoContent> Remove(
        Guid id,
        HttpContext context,
        ShareService shares,
        CancellationToken ct
    )
    {
        await shares.RemoveAsync(context.Actor(), id, ct);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> Leave(
        Guid id,
        HttpContext context,
        ShareService shares,
        CancellationToken ct
    )
    {
        await shares.LeaveAsync(context.Actor(), id, ct);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> Seen(
        Guid id,
        HttpContext context,
        ShareService shares,
        CancellationToken ct
    )
    {
        await shares.MarkSeenAsync(context.Actor(), id, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<List<SharedItemDto>>> SharedWithMe(
        HttpContext context,
        ShareService shares,
        NodeDtoMapper mapper,
        CancellationToken ct
    )
    {
        var items = await shares.SharedWithMeAsync(context.Actor(), ct);
        var dtos = await mapper.MapAsync(items.Select(i => i.Node).ToList(), ct);
        return TypedResults.Ok(
            items
                .Select(
                    (item, i) =>
                        new SharedItemDto(
                            dtos[i],
                            item.Share.CreatedAt,
                            item.Share.Role,
                            item.Share.SeenAt is null
                        )
                )
                .ToList()
        );
    }

    private static async Task<Ok<MoveCheckResponse>> MoveCheck(
        MoveCheckRequest request,
        HttpContext context,
        ShareService shares,
        CancellationToken ct
    )
    {
        if (request.Ids.Count is 0 or > 1000)
        {
            throw new DriveException(DriveError.InvalidRequest, "Give 1 to 1000 ids.");
        }

        var losing = await shares.LosingAccessAsync(
            context.Actor(),
            request.Ids,
            request.FolderId,
            ct
        );
        return TypedResults.Ok(new MoveCheckResponse(losing.Select(PersonDto.From).ToList()));
    }
}

public sealed record PersonDto(Guid Id, string Name, string? Email)
{
    public static PersonDto From(User user) => new(user.Id, user.DisplayName, user.Email);
}

/// <param name="YourAccess">What the person asking may do. Only the owner can change who has access.</param>
/// <param name="People">Everyone it is shared with, through this node or a folder above it, by name. The owner isn't listed.</param>
public sealed record AccessDto(PersonDto Owner, NodeRole YourAccess, List<AccessPersonDto> People)
{
    public static AccessDto From(NodeAccessInfo info) =>
        new(
            PersonDto.From(info.Owner),
            info.YourAccess switch
            {
                Access.Own => NodeRole.Owner,
                Access.Edit => NodeRole.Editor,
                _ => NodeRole.Viewer,
            },
            info.People.Select(p => new AccessPersonDto(
                    PersonDto.From(p.Person),
                    p.Role,
                    p.Direct?.Id,
                    p.InheritedRole,
                    p.InheritedFrom is { } from ? new PathEntry(from.Id, from.Name.Value) : null
                ))
                .ToList()
        );
}

/// <param name="Role">What they can do here: the higher of this node's share and what a folder above gives.</param>
/// <param name="ShareId">The share on this node itself, which the owner can change or remove. Null when it all comes from above.</param>
/// <param name="InheritedRole">What a folder above gives them: the least a share here may be.</param>
/// <param name="InheritedFrom">That folder, when the person asking can see it.</param>
public sealed record AccessPersonDto(
    PersonDto Person,
    ShareRole Role,
    Guid? ShareId,
    ShareRole? InheritedRole,
    PathEntry? InheritedFrom
);

public sealed record ShareRequest(Guid UserId, ShareRole Role);

public sealed record ChangeShareRequest(ShareRole Role);

/// <param name="New">Not opened yet since it was shared.</param>
public sealed record SharedItemDto(NodeDto Node, DateTimeOffset SharedAt, ShareRole Role, bool New);

/// <param name="FolderId">Where they would go; null for the top of the person's own My files.</param>
public sealed record MoveCheckRequest(List<Guid> Ids, Guid? FolderId);

/// <param name="LosingAccess">People who can see some of the items now and couldn't after the move.</param>
public sealed record MoveCheckResponse(List<PersonDto> LosingAccess);
