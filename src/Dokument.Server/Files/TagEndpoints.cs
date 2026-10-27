using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Tags;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Dokument.Server.Files;

/// <summary>Tags on files (docs/design.md §13.4). Shared: everyone who can see a file sees its tags.</summary>
public static class TagEndpoints
{
    public static RouteGroupBuilder MapTagEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("").WithTags("Tags").RequireAuthorization();
        group
            .MapGet("/nodes/{id:guid}/tags", Get)
            .WithName("GetFileTags")
            .WithDescription(
                "A file's tags, and the automatic tags someone took off it, for the details panel."
            );
        group
            .MapPost("/nodes/tags", Change)
            .WithName("ChangeTags")
            .WithDescription(
                "Adds and removes tags on one or more files at once. Removing an automatic tag is remembered for "
                    + "each file, so keyword rules don't add it back."
            );
        group
            .MapGet("/tags", Suggest)
            .WithName("SuggestTags")
            .WithDescription(
                "Tags on files the person can see, most used first, with how many files have each. `q` keeps "
                    + "those that start with it, or have a word that does."
            );
        return api;
    }

    private static async Task<Ok<FileTags>> Get(
        Guid id,
        HttpContext context,
        DriveService drive,
        TagService tags,
        CancellationToken ct
    )
    {
        var file = await drive.GetFileAsync(context.Actor(), id, ct);
        var shown = await tags.ForNodesAsync([file.Id], ct);
        return TypedResults.Ok(
            new FileTags(
                shown.TryGetValue(file.Id, out var list)
                    ? list.Select(t => new TagDto(t.Name, t.Source, t.RuleId)).ToList()
                    : [],
                await tags.RemovedAsync(file, ct)
            )
        );
    }

    private static async Task<NoContent> Change(
        ChangeTagsRequest request,
        HttpContext context,
        TagService tags,
        CancellationToken ct
    )
    {
        await tags.ChangeAsync(
            context.Actor(),
            request.Ids,
            request.Add ?? [],
            request.Remove ?? [],
            ct
        );
        return TypedResults.NoContent();
    }

    private static async Task<Ok<TagSuggestions>> Suggest(
        HttpContext context,
        TagService tags,
        CancellationToken ct,
        string? q = null,
        int limit = 10
    )
    {
        var found = await tags.SuggestAsync(context.Actor(), q, limit, ct);
        return TypedResults.Ok(
            new TagSuggestions(found.Select(t => new TagSuggestion(t.Name, t.Files)).ToList())
        );
    }
}
