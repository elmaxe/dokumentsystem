using Dokument.Domain.Search;
using Dokument.Domain.Tags;
using Dokument.Infrastructure.Tags;
using Dokument.Server.Auth;
using Dokument.Server.Files;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Dokument.Server.Admin;

/// <param name="Words">As the admin typed them, tidied: entries separated by ", ", phrases in quotes.</param>
/// <param name="Files">Live files that show the tag because of this rule now.</param>
/// <param name="Running">The rule is being run over every file after a change; <paramref name="Files"/> is still growing.</param>
public sealed record TagRuleDto(
    Guid Id,
    string Tag,
    string Words,
    bool Enabled,
    int Files,
    bool Running,
    DateTimeOffset UpdatedAt
)
{
    public static TagRuleDto From(TagRule rule, int files, bool running) =>
        new(
            rule.Id,
            rule.Tag.Value,
            rule.Words.Value,
            rule.Enabled,
            files,
            running,
            rule.UpdatedAt
        );
}

public sealed record TagRuleListing(List<TagRuleDto> Rules);

/// <param name="Words">Separated by commas; "a phrase" in quotes. A word also matches its Swedish and English forms; typos don't match.</param>
public sealed record TagRuleRequest(string Tag, string Words, bool Enabled = true);

/// <param name="Tag">Leaves out files someone took this tag off, when given.</param>
/// <param name="Offset">How many matching files to skip, for the next page.</param>
/// <param name="Limit">Files per page, 1 to 100.</param>
public sealed record TagRulePreviewRequest(
    string? Tag,
    string Words,
    int Offset = 0,
    int Limit = 50
);

/// <param name="Owner">The owner's name; admins see every owner's files.</param>
/// <param name="Snippet">Where the rule matched, with the words marked.</param>
public sealed record TagRuleMatchDto(
    Guid NodeId,
    string Name,
    string? MimeType,
    string Owner,
    SnippetDto? Snippet
);

/// <param name="Files">Live files, of every owner, that would show the tag.</param>
/// <param name="Matches">One page of them, newest first.</param>
public sealed record TagRulePreviewDto(int Files, List<TagRuleMatchDto> Matches);

/// <summary>Keyword rules (docs/design.md §13.4), on the admin Tags page. Admins only.</summary>
public static class AdminTagEndpoints
{
    public static RouteGroupBuilder MapAdminTagEndpoints(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin/tag-rules")
            .WithTags("Admin")
            .RequireAuthorization(SessionAuthentication.AdminPolicy);
        admin.MapGet("", List).WithName("AdminListTagRules");
        admin
            .MapPost("", Create)
            .WithName("AdminCreateTagRule")
            .WithDescription("Adds a rule and runs it over every stored file in the background.");
        admin
            .MapPut("/{id:guid}", Update)
            .WithName("AdminUpdateTagRule")
            .WithDescription(
                "Changing the tag or words runs the rule again over every file in the background; turning it "
                    + "off takes its tags off at once."
            );
        admin
            .MapDelete("/{id:guid}", Delete)
            .WithName("AdminDeleteTagRule")
            .WithDescription("Deletes the rule and takes its tags off every file.");
        admin
            .MapPost("/preview", Preview)
            .WithName("AdminPreviewTagRule")
            .WithDescription(
                "How many files a rule would tag, and which, a page at a time. Changes nothing."
            );
        return api;
    }

    private static async Task<Ok<TagRuleListing>> List(TagRules rules, CancellationToken ct) =>
        TypedResults.Ok(
            new TagRuleListing(
                (await rules.ListAsync(ct))
                    .Select(r => TagRuleDto.From(r.Rule, r.Files, r.Running))
                    .ToList()
            )
        );

    private static async Task<Ok<TagRuleDto>> Create(
        TagRuleRequest request,
        HttpContext context,
        TagRules rules,
        CancellationToken ct
    )
    {
        var rule = await rules.CreateAsync(
            context.Actor(),
            request.Tag,
            request.Words,
            request.Enabled,
            ct
        );
        return TypedResults.Ok(TagRuleDto.From(rule, 0, rule.Enabled));
    }

    private static async Task<Ok<TagRuleDto>> Update(
        Guid id,
        TagRuleRequest request,
        HttpContext context,
        TagRules rules,
        CancellationToken ct
    )
    {
        await rules.UpdateAsync(
            context.Actor(),
            id,
            request.Tag,
            request.Words,
            request.Enabled,
            ct
        );
        var summary = (await rules.ListAsync(ct)).Single(r => r.Rule.Id == id);
        return TypedResults.Ok(TagRuleDto.From(summary.Rule, summary.Files, summary.Running));
    }

    private static async Task<NoContent> Delete(
        Guid id,
        HttpContext context,
        TagRules rules,
        CancellationToken ct
    )
    {
        await rules.DeleteAsync(context.Actor(), id, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<TagRulePreviewDto>> Preview(
        TagRulePreviewRequest request,
        TagRules rules,
        CancellationToken ct
    )
    {
        var preview = await rules.PreviewAsync(
            request.Tag,
            request.Words,
            request.Offset,
            request.Limit,
            ct
        );
        return TypedResults.Ok(
            new TagRulePreviewDto(
                preview.Files,
                preview
                    .Matches.Select(e => new TagRuleMatchDto(
                        e.NodeId,
                        e.Name,
                        e.MimeType,
                        e.OwnerName,
                        e.Snippet is Snippet s
                            ? new SnippetDto(s.Text, [.. s.Highlights], s.CutStart, s.CutEnd)
                            : null
                    ))
                    .ToList()
            )
        );
    }
}
