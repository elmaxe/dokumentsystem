using Dokument.Domain.Filing;
using Dokument.Infrastructure.Filing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Dokument.Server.Files;

/// <param name="Folder">The folder template, from the top of My files, such as "Kvitton/{year}".</param>
/// <param name="Filed">Files the rule moved that weren't moved back with Undo.</param>
/// <param name="Skipped">Files the rule left where they were, because the folder would hide them from people they are shared with (§13.6).</param>
/// <param name="Running">A run of the rule, or of all your rules, is going.</param>
public sealed record FilingRuleDto(
    Guid Id,
    string Tag,
    string Folder,
    bool Enabled,
    int Filed,
    int Skipped,
    bool Running
)
{
    public static FilingRuleDto From(FilingRule rule, int filed, int skipped, bool running) =>
        new(rule.Id, rule.Tag.Value, rule.Folder.Value, rule.Enabled, filed, skipped, running);
}

public sealed record FilingRuleListing(List<FilingRuleDto> Rules);

/// <param name="Folder">Folders from the top of My files separated by "/", which may hold {year} and {month}.</param>
/// <param name="FileExisting">Also file the files that have the tag now, with a run of the rule. Otherwise only files that get it from now on move.</param>
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

/// <param name="RuleId">Run only this rule. Left out, every rule of yours that is on runs, first match first.</param>
/// <param name="NodeIds">Run on these files, and on everything in these folders. Left out, on all your files.</param>
/// <param name="IncludePlaced">Also move files you put somewhere after a rule filed them, or moved back with Undo.</param>
public sealed record FilingRunRequest(
    Guid? RuleId = null,
    List<Guid>? NodeIds = null,
    bool IncludePlaced = false
);

/// <param name="From">Where it is now, as "Inkorg / Scans", or "" for the top of My files.</param>
public sealed record RunFileDto(Guid NodeId, string Name, string? MimeType, string From);

/// <param name="Folder">Where the files would go, as "Kvitton / 2026".</param>
/// <param name="NewFolder">The folder, or one above it, would be made.</param>
/// <param name="Files">The first 100 of them.</param>
public sealed record RunGroupDto(string Folder, bool NewFolder, int Count, List<RunFileDto> Files);

/// <summary>What a run of your filing rules would do. Nothing has moved.</summary>
/// <param name="Move">Files that would move, by folder in <paramref name="Groups"/>.</param>
/// <param name="AlreadyThere">Files already in their rule's folder.</param>
/// <param name="Placed">Files you put somewhere after a rule filed them, or moved back with Undo, which stay unless you include them.</param>
/// <param name="Shared">Files that stay because the folder would hide them from people they are shared with.</param>
/// <param name="Blocked">Files that stay because a file has the name of one of the folders.</param>
/// <param name="NoRule">Your files in the run without a tag of its rules.</param>
/// <param name="NotYours">Files in the run that someone else owns. Rules only move their owner's files.</param>
public sealed record RunPreviewDto(
    int Move,
    List<RunGroupDto> Groups,
    int AlreadyThere,
    int Placed,
    int Shared,
    int Blocked,
    int NoRule,
    int NotYours
);

/// <param name="RuleTag">The tag of the one rule it runs, or null for all your rules.</param>
/// <param name="Total">Files it will move, once it has looked.</param>
/// <param name="Done">Files it has gone through.</param>
/// <param name="Folders">The folders it moved files to.</param>
/// <param name="Folder">The one folder it moved files to, as "Kvitton / 2026", when there is one.</param>
/// <param name="CanUndo">It moved files and can still be undone.</param>
public sealed record FilingRunDto(
    Guid Id,
    Guid? RuleId,
    string? RuleTag,
    FilingRunStatus Status,
    bool StopRequested,
    int Total,
    int Done,
    int Moved,
    int Folders,
    Guid? FolderId,
    string? Folder,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt,
    DateTimeOffset? UndoneAt,
    bool CanUndo
)
{
    public static FilingRunDto From(FilingRunSummary s) =>
        new(
            s.Run.Id,
            s.Run.RuleId,
            s.RuleTag,
            s.Run.Status,
            s.Run.StopRequested,
            s.Run.Total,
            s.Run.Done,
            s.Run.Moved,
            s.Folders,
            s.FolderId,
            s.Folder,
            s.Run.CreatedAt,
            s.Run.FinishedAt,
            s.Run.UndoneAt,
            s.CanUndo
        );
}

/// <param name="Files">Files that went back where they were.</param>
public sealed record UndoRunResult(int Files);

/// <summary>Each person's own filing rules (docs/design.md §13.6), in their Settings, and Undo for what they moved.</summary>
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

        var runs = api.MapGroup("/me/filing-runs").WithTags("Filing").RequireAuthorization();
        runs.MapPost("/preview", PreviewRun)
            .WithName("PreviewFilingRun")
            .WithDescription(
                "What running your filing rules over files you already have would move, by folder. Changes nothing."
            );
        runs.MapPost("", StartRun)
            .WithName("StartFilingRun")
            .WithDescription(
                "Runs your filing rules over files you already have, in the background. One run at a time."
            );
        runs.MapGet("/latest", LatestRun)
            .WithName("GetLatestFilingRun")
            .WithDescription("Your latest run, or 204 when you have none.");
        runs.MapGet("/{id:guid}", GetRun).WithName("GetFilingRun");
        runs.MapPost("/{id:guid}/stop", StopRun)
            .WithName("StopFilingRun")
            .WithDescription("Stops a run after the file it is on. What it moved stays moved.");
        runs.MapPost("/{id:guid}/undo", UndoRun)
            .WithName("UndoFilingRun")
            .WithDescription(
                "Moves the files a run moved back where they were, unless you have moved them since."
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
                    .Select(r => FilingRuleDto.From(r.Rule, r.Filed, r.Skipped, r.Running))
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
        return TypedResults.Ok(
            FilingRuleDto.From(rule, 0, 0, rule.Enabled && request.FileExisting)
        );
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
        return TypedResults.Ok(
            FilingRuleDto.From(summary.Rule, summary.Filed, summary.Skipped, summary.Running)
        );
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

    private static async Task<Ok<RunPreviewDto>> PreviewRun(
        FilingRunRequest request,
        HttpContext context,
        FilingService filing,
        CancellationToken ct
    )
    {
        var p = await filing.PreviewRunAsync(
            context.Actor(),
            request.RuleId,
            request.NodeIds,
            request.IncludePlaced,
            ct
        );
        return TypedResults.Ok(
            new RunPreviewDto(
                p.Move,
                p.Groups.Select(g => new RunGroupDto(
                        g.Folder,
                        g.NewFolder,
                        g.Count,
                        g.Files.Select(f => new RunFileDto(f.NodeId, f.Name, f.MimeType, f.From))
                            .ToList()
                    ))
                    .ToList(),
                p.AlreadyThere,
                p.Placed,
                p.Shared,
                p.Blocked,
                p.NoRule,
                p.NotYours
            )
        );
    }

    private static async Task<Ok<FilingRunDto>> StartRun(
        FilingRunRequest request,
        HttpContext context,
        FilingService filing,
        CancellationToken ct
    )
    {
        var run = await filing.StartRunAsync(
            context.Actor(),
            request.RuleId,
            request.NodeIds,
            request.IncludePlaced,
            ct
        );
        return TypedResults.Ok(
            FilingRunDto.From((await filing.GetRunAsync(context.Actor(), run.Id, ct))!)
        );
    }

    private static async Task<Results<Ok<FilingRunDto>, NoContent>> LatestRun(
        HttpContext context,
        FilingService filing,
        CancellationToken ct
    ) =>
        await filing.GetRunAsync(context.Actor(), null, ct) is { } run
            ? TypedResults.Ok(FilingRunDto.From(run))
            : TypedResults.NoContent();

    private static async Task<Ok<FilingRunDto>> GetRun(
        Guid id,
        HttpContext context,
        FilingService filing,
        CancellationToken ct
    ) => TypedResults.Ok(FilingRunDto.From((await filing.GetRunAsync(context.Actor(), id, ct))!));

    private static async Task<NoContent> StopRun(
        Guid id,
        HttpContext context,
        FilingService filing,
        CancellationToken ct
    )
    {
        await filing.StopRunAsync(context.Actor(), id, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<UndoRunResult>> UndoRun(
        Guid id,
        HttpContext context,
        FilingService filing,
        CancellationToken ct
    ) => TypedResults.Ok(new UndoRunResult(await filing.UndoRunAsync(context.Actor(), id, ct)));
}
