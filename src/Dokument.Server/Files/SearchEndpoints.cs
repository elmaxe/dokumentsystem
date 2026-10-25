using Dokument.Domain.Files;
using Dokument.Domain.Search;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Search;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Dokument.Server.Files;

/// <param name="Highlights">Ranges to mark, as <c>[start, end)</c> offsets into <paramref name="Text"/>.</param>
/// <param name="CutStart">The page's text goes on before the snippet; show an ellipsis.</param>
public sealed record SnippetDto(string Text, List<int[]> Highlights, bool CutStart, bool CutEnd);

/// <param name="Path">The folders above the file, top first.</param>
/// <param name="Source">Where the best hit was. The name's own hits are marked by the client using <c>terms</c>.</param>
/// <param name="PageNo">The best page, from 1, for files with pages; open the viewer there.</param>
/// <param name="PageCount">Pages (or slides) in the file, when it has them.</param>
/// <param name="PagesWithHits">How many pages matched.</param>
public sealed record SearchResultDto(
    NodeDto Node,
    List<PathEntry> Path,
    MatchSource Source,
    int? PageNo,
    int? PageCount,
    int PagesWithHits,
    SnippetDto? Snippet
);

/// <param name="Total">All matches; <c>results</c> is one page of them.</param>
/// <param name="Terms">The words to mark, as the server understood the query.</param>
/// <param name="Pending">Files whose text the worker is still reading. They may match once it has.</param>
/// <param name="WithoutFilters">With no results: how many there would be without the filters. Null otherwise.</param>
public sealed record SearchResponse(
    List<SearchResultDto> Results,
    int Total,
    List<string> Terms,
    int Pending,
    int? WithoutFilters
);

public sealed record SuggestionDto(NodeDto Node, List<PathEntry> Path);

public sealed record SuggestResponse(List<SuggestionDto> Items, List<string> Terms);

/// <param name="Boxes">Hits in recognized text, as <c>[x, y, width, height]</c> fractions of the page. Empty for a text layer, which the viewer searches itself.</param>
public sealed record PageMatchDto(int PageNo, PageSource Source, List<double[]> Boxes);

public sealed record MatchesResponse(List<string> Terms, List<PageMatchDto> Pages);

/// <summary>Search (docs/design.md §13). Only what the person could open is ever found, and queries are never logged.</summary>
public static class SearchEndpoints
{
    public static RouteGroupBuilder MapSearchEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("").WithTags("Search").RequireAuthorization();
        group
            .MapGet("/search", Search)
            .WithName("Search")
            .WithDescription(
                "Files and folders whose name, text or metadata match. `q` takes words, \"exact phrases\", -excluded "
                    + "words and the operators type:, in:, after:, before:, camera:, taken: and is:starred; the other "
                    + "parameters are the filter chips and override the operators."
            );
        group
            .MapGet("/search/suggest", Suggest)
            .WithName("SearchSuggest")
            .WithDescription("Name matches as someone types, best first.");
        group
            .MapGet("/nodes/{id:guid}/matches", Matches)
            .WithName("SearchMatchesInFile")
            .WithDescription(
                "The pages of one file that match `q`, for the viewer to open on and step through."
            );
        return api;
    }

    private static async Task<Results<Ok<SearchResponse>, ValidationProblem>> Search(
        HttpContext context,
        SearchService search,
        DriveService drive,
        NodeDtoMapper mapper,
        CancellationToken ct,
        string? q = null,
        string? type = null,
        DateTimeOffset? modifiedAfter = null,
        DateTimeOffset? modifiedBefore = null,
        Guid? folderId = null,
        bool starred = false,
        SearchSort sort = SearchSort.Relevance,
        int offset = 0,
        int limit = 50
    )
    {
        FileType? fileType = null;
        if (!string.IsNullOrEmpty(type) && (fileType = FileType.TryParse(type)) is null)
        {
            return TypedResults.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    ["type"] =
                    [
                        $"Use one of {string.Join(", ", FileType.All.Select(t => t.Name))}.",
                    ],
                }
            );
        }

        var results = await search.SearchAsync(
            context.Actor(),
            new SearchRequest(
                q,
                fileType,
                modifiedAfter,
                modifiedBefore,
                folderId,
                starred,
                sort,
                Math.Max(0, offset),
                limit
            ),
            ct
        );
        var nodes = results.Hits.Select(h => h.Node).ToList();
        var dtos = await mapper.MapAsync(nodes, ct);
        var paths = await PathsAsync(drive, nodes, ct);
        return TypedResults.Ok(
            new SearchResponse(
                results
                    .Hits.Select(
                        (h, i) =>
                            new SearchResultDto(
                                dtos[i],
                                paths[h.Node.Id],
                                h.Source,
                                h.PageNo,
                                h.PageCount,
                                h.PagesWithHits,
                                h.Snippet is { } s
                                    ? new SnippetDto(
                                        s.Text,
                                        [.. s.Highlights],
                                        s.CutStart,
                                        s.CutEnd
                                    )
                                    : null
                            )
                    )
                    .ToList(),
                results.Total,
                [.. results.Terms],
                results.Pending,
                results.WithoutFilters
            )
        );
    }

    private static async Task<Ok<SuggestResponse>> Suggest(
        HttpContext context,
        SearchService search,
        DriveService drive,
        NodeDtoMapper mapper,
        CancellationToken ct,
        string? q = null
    )
    {
        var nodes = await search.SuggestAsync(context.Actor(), q, limit: 6, ct);
        var dtos = await mapper.MapAsync(nodes, ct);
        var paths = await PathsAsync(drive, nodes, ct);
        return TypedResults.Ok(
            new SuggestResponse(
                nodes.Select((n, i) => new SuggestionDto(dtos[i], paths[n.Id])).ToList(),
                [.. SearchQuery.Parse(q).HighlightTerms]
            )
        );
    }

    private static async Task<Ok<MatchesResponse>> Matches(
        Guid id,
        HttpContext context,
        DriveService drive,
        SearchService search,
        CancellationToken ct,
        string? q = null
    )
    {
        var file = await drive.GetFileAsync(context.Actor(), id, ct);
        var (terms, pages) = await search.MatchesInAsync(file, q, ct);
        return TypedResults.Ok(
            new MatchesResponse(
                [.. terms],
                pages.Select(p => new PageMatchDto(p.PageNo, p.Source, [.. p.Boxes])).ToList()
            )
        );
    }

    private static async Task<Dictionary<Guid, List<PathEntry>>> PathsAsync(
        DriveService drive,
        List<Node> nodes,
        CancellationToken ct
    ) =>
        (await drive.AncestorsAsync(nodes, ct)).ToDictionary(
            a => a.Key,
            a => a.Value.Select(f => new PathEntry(f.Id, f.Name.Value)).ToList()
        );
}
