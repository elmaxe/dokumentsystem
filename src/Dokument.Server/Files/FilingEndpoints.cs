using Dokument.Domain.Filing;
using Dokument.Infrastructure.Filing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Dokument.Server.Files;

/// <param name="Folder">The folder template, from the top of My files, such as "Kvitton/{year}".</param>
/// <param name="Filed">Files the rule moved that weren't moved back with Undo.</param>
/// <param name="Running">The rule is filing the files that already had its tag.</param>
public sealed record FilingRuleDto(
    Guid Id,
    string Tag,
    string Folder,
    bool Enabled,
    int Filed,
    bool Running
)
{
    public static FilingRuleDto From(FilingRule rule, int filed, bool running) =>
        new(rule.Id, rule.Tag.Value, rule.Folder.Value, rule.Enabled, filed, running);
}

public sealed record FilingRuleListing(List<FilingRuleDto> Rules);

/// <param name="Folder">Folders from the top of My files separated by "/", which may hold {year} and {month}.</param>
/// <param name="FileExisting">Also file the files that have the tag now. Otherwise only files that get it from now on move.</param>
public sealed record FilingRuleRequest(
    string Tag,
    string Folder,
    bool Enabled = true,
    bool FileExisting = false
);

public sealed record FilingRuleOrderRequest(List<Guid> Ids);

public sealed record FilingPreviewRequest(string? Tag, string? Folder);

/// <param name="Folder">Where it would go, as "Kvitton / 2026".</param>
public sealed record FilingExampleDto(Guid NodeId, string Name, string? MimeType, string Folder);

/// <param name="Files">Your files that have the tag now and that no rule has filed.</param>
public sealed record FilingPreviewDto(int Files, List<FilingExampleDto> Examples);

/// <param name="Folder">Where it went, as "Kvitton / 2026".</param>
public sealed record FiledFileDto(
    Guid NodeId,
    string Name,
    string? MimeType,
    Guid? FolderId,
    string Folder,
    string Tag,
    DateTimeOffset FiledAt
);

public sealed record FiledFiles(List<FiledFileDto> Items);

/// <summary>Each person's own filing rules (docs/design.md §13.5), in their Settings, and Undo for what they moved.</summary>
public static class FilingEndpoints
{
    public static RouteGroupBuilder MapFilingEndpoints(this RouteGroupBuilder api)
    {
        var rules = api.MapGroup("/me/filing-rules").WithTags("Filing").RequireAuthorization();
        rules.MapGet("", List).WithName("ListFilingRules");
        rules
            .MapPost("", Create)
            .WithName("CreateFilingRule")
            .WithDescription(
                "Adds a rule at the end of the list: when one of your files gets the tag, it moves to the folder."
            );
        rules.MapPut("/{id:guid}", Update).WithName("UpdateFilingRule");
        rules
            .MapDelete("/{id:guid}", Delete)
            .WithName("DeleteFilingRule")
            .WithDescription("Deletes a rule. Files it moved stay where they are.");
        rules
            .MapPut("/order", Reorder)
            .WithName("ReorderFilingRules")
            .WithDescription(
                "Puts all your rules in this order. The first that matches a file wins."
            );
        rules
            .MapPost("/preview", Preview)
            .WithName("PreviewFilingRule")
            .WithDescription(
                "How many of your files have the tag now and would be filed, with where a few would go. Changes nothing."
            );

        var filings = api.MapGroup("/filings").WithTags("Filing").RequireAuthorization();
        filings
            .MapGet("/recent", Recent)
            .WithName("ListRecentFilings")
            .WithDescription(
                "Files your rules moved after `after` (by default, the last two minutes) that are still there, oldest first."
            );
        filings
            .MapPost("/{id:guid}/undo", Undo)
            .WithName("UndoFiling")
            .WithDescription("Moves a filed file back where it was. No rule files it again.");
        return api;
    }

    private static async Task<Ok<FilingRuleListing>> List(
        HttpContext context,
        FilingService filing,
        CancellationToken ct
    ) =>
        TypedResults.Ok(
            new FilingRuleListing(
                (await filing.ListAsync(context.Actor(), ct))
                    .Select(r => FilingRuleDto.From(r.Rule, r.Filed, r.Running))
                    .ToList()
            )
        );

    private static async Task<Ok<FilingRuleDto>> Create(
        FilingRuleRequest request,
        HttpContext context,
        FilingService filing,
        CancellationToken ct
    )
    {
        var rule = await filing.CreateAsync(
            context.Actor(),
            request.Tag,
            request.Folder,
            request.Enabled,
            request.FileExisting,
            ct
        );
        return TypedResults.Ok(FilingRuleDto.From(rule, 0, rule.Enabled && request.FileExisting));
    }

    private static async Task<Ok<FilingRuleDto>> Update(
        Guid id,
        FilingRuleRequest request,
        HttpContext context,
        FilingService filing,
        CancellationToken ct
    )
    {
        await filing.UpdateAsync(
            context.Actor(),
            id,
            request.Tag,
            request.Folder,
            request.Enabled,
            request.FileExisting,
            ct
        );
        var summary = (await filing.ListAsync(context.Actor(), ct)).Single(r => r.Rule.Id == id);
        return TypedResults.Ok(FilingRuleDto.From(summary.Rule, summary.Filed, summary.Running));
    }

    private static async Task<NoContent> Delete(
        Guid id,
        HttpContext context,
        FilingService filing,
        CancellationToken ct
    )
    {
        await filing.DeleteAsync(context.Actor(), id, ct);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> Reorder(
        FilingRuleOrderRequest request,
        HttpContext context,
        FilingService filing,
        CancellationToken ct
    )
    {
        await filing.ReorderAsync(context.Actor(), request.Ids, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<FilingPreviewDto>> Preview(
        FilingPreviewRequest request,
        HttpContext context,
        FilingService filing,
        CancellationToken ct
    )
    {
        var preview = await filing.PreviewAsync(context.Actor(), request.Tag, request.Folder, ct);
        return TypedResults.Ok(
            new FilingPreviewDto(
                preview.Files,
                preview
                    .Examples.Select(e => new FilingExampleDto(
                        e.NodeId,
                        e.Name,
                        e.MimeType,
                        e.Folder
                    ))
                    .ToList()
            )
        );
    }

    private static async Task<Ok<FiledFiles>> Recent(
        HttpContext context,
        FilingService filing,
        CancellationToken ct,
        DateTimeOffset? after = null
    ) =>
        TypedResults.Ok(
            new FiledFiles(
                (await filing.RecentAsync(context.Actor(), after, ct))
                    .Select(f => new FiledFileDto(
                        f.NodeId,
                        f.Name,
                        f.MimeType,
                        f.FolderId,
                        f.Folder,
                        f.Tag,
                        f.FiledAt
                    ))
                    .ToList()
            )
        );

    private static async Task<NoContent> Undo(
        Guid id,
        HttpContext context,
        FilingService filing,
        CancellationToken ct
    )
    {
        await filing.UndoAsync(context.Actor(), id, ct);
        return TypedResults.NoContent();
    }
}
