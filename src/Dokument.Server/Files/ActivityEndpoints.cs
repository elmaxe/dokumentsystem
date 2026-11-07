using Dokument.Domain.Sharing;
using Dokument.Infrastructure.Files;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Dokument.Server.Files;

/// <summary>The Activity tab of the details panel (docs/design.md §18).</summary>
public static class ActivityEndpoints
{
    public static RouteGroupBuilder MapActivityEndpoints(this RouteGroupBuilder api)
    {
        api.MapGroup("")
            .WithTags("Files")
            .RequireAuthorization()
            .MapGet("/nodes/{id:guid}/activity", List)
            .WithName("ListActivity")
            .WithDescription(
                "What happened to a file, or to a folder and everything in it, newest first. Everyone who can see it "
                    + "sees its changes; downloads only show for the owner, and for each person their own. Pass the "
                    + "last entry's `at` as `before` for more."
            );
        return api;
    }

    private static async Task<Ok<ActivityListing>> List(
        Guid id,
        HttpContext context,
        ActivityService activity,
        CancellationToken ct,
        DateTimeOffset? before = null,
        int limit = 30
    )
    {
        var result = await activity.ListAsync(context.Actor(), id, before, limit, ct);
        return TypedResults.Ok(
            new ActivityListing(result.Entries.Select(ActivityDto.Of).ToList(), result.More)
        );
    }
}

public sealed record ActivityListing(List<ActivityDto> Entries, bool More);

/// <summary>Where something was moved from or to.</summary>
/// <param name="Folder">The folder, when the person can see it.</param>
/// <param name="Top">The top of the owner's files.</param>
public sealed record ActivityPlaceDto(PathEntry? Folder, bool Top);

/// <param name="Action">The audit action, such as <c>node.rename</c> or <c>share.grant</c>.</param>
/// <param name="Actor">Who did it; null when the system did, such as a filing rule, or someone with a public link.</param>
/// <param name="ViaLink">Done by someone with a public link, such as a download.</param>
/// <param name="NodeName">The item's name now; null once it's deleted forever.</param>
/// <param name="FromName">A rename's old name.</param>
/// <param name="ToName">A rename's new name.</param>
/// <param name="Tag">The tag of the filing rule that moved it.</param>
/// <param name="Person">Who a share was given to, changed for or taken from.</param>
/// <param name="VersionNumber">The version a restore brought back.</param>
public sealed record ActivityDto(
    Guid Id,
    DateTimeOffset At,
    string Action,
    PersonDto? Actor,
    bool ViaLink,
    Guid NodeId,
    string? NodeName,
    bool IsFolder,
    string? FromName,
    string? ToName,
    ActivityPlaceDto? From,
    ActivityPlaceDto? To,
    string? Tag,
    List<string>? Added,
    List<string>? Removed,
    PersonDto? Person,
    ShareRole? Role,
    int? VersionNumber
)
{
    public static ActivityDto Of(ActivityEntry e) =>
        new(
            e.Id,
            e.At,
            e.Action,
            e.Actor is { } actor ? PersonDto.From(actor) : null,
            e.ViaLink,
            e.NodeId,
            e.Node?.Name.Value,
            e.Node?.Kind == Domain.Files.NodeKind.Folder,
            e.FromName,
            e.ToName,
            Place(e.From),
            Place(e.To),
            e.Tag,
            e.Added,
            e.Removed,
            e.Person is { } person ? PersonDto.From(person) : null,
            Enum.TryParse<ShareRole>(e.Role, ignoreCase: true, out var role) ? role : null,
            e.VersionNumber
        );

    private static ActivityPlaceDto? Place(ActivityPlace? place) =>
        place switch
        {
            null => null,
            { Id: { } id, Name: { } name } => new ActivityPlaceDto(
                new PathEntry(id, name),
                Top: false
            ),
            { Visible: true } => new ActivityPlaceDto(null, Top: true),
            _ => new ActivityPlaceDto(null, Top: false),
        };
}
