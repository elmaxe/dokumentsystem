using System.Diagnostics;
using Dokument.Application.Files;
using Dokument.Domain.Files;
using Dokument.Domain.Jobs;
using Dokument.Domain.Search;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Dokument.Infrastructure.Search;

public enum SearchSort
{
    Relevance,
    Modified,
}

/// <summary>What the search bar sent: the typed text (operators included) plus the filter chips.</summary>
public sealed record SearchRequest(
    string? Q,
    FileType? Type = null,
    DateTimeOffset? ModifiedAfter = null,
    DateTimeOffset? ModifiedBefore = null,
    Guid? FolderId = null,
    bool Starred = false,
    SearchSort Sort = SearchSort.Relevance,
    int Offset = 0,
    int Limit = 50
);

/// <summary>Where a file's best hit was: its name, its text, text recognized in a scan or photo, or its metadata.</summary>
public enum MatchSource
{
    Name,
    Text,
    Ocr,
    Metadata,
}

/// <param name="PageNo">The best page, from 1; null for a name match or a format without pages.</param>
/// <param name="PageCount">Pages or slides in the file, when it has them.</param>
/// <param name="PagesWithHits">How many pages matched, for "3 pages" in the result row.</param>
public sealed record SearchHit(
    Node Node,
    MatchSource Source,
    int? PageNo,
    int? PageCount,
    int PagesWithHits,
    Snippet? Snippet
);

/// <param name="Terms">The words to mark in names, snippets and the viewer.</param>
/// <param name="Pending">Files in scope whose text or metadata the worker hasn't read yet; they may match once it has.</param>
/// <param name="WithoutFilters">With no results, how many there would be without the filters; null otherwise.</param>
public sealed record SearchResults(
    IReadOnlyList<SearchHit> Hits,
    int Total,
    IReadOnlyList<string> Terms,
    int Pending,
    int? WithoutFilters
);

/// <summary>A page of a file that matched, for the viewer: where the hits are, so it can open on the first and step through.</summary>
/// <param name="Boxes">For recognized text, each hit's box as fractions of the page: <c>[x, y, w, h]</c>.</param>
public sealed record PageMatch(int PageNo, PageSource Source, IReadOnlyList<double[]> Boxes);

/// <summary>
/// Search (docs/design.md §13): BM25 over names and page text inside Postgres, then the same visibility rule as
/// browsing (§9) through <see cref="NodeAccess"/>, so a person only ever finds what they could open. Results are files,
/// not pages; each shows its best page. Queries are never logged or audited.
/// </summary>
public sealed class SearchService(DokumentDbContext db)
{
    /// <summary>The most files one search ranks; more than anyone scrolls through.</summary>
    private const int MaxCandidates = 1000;

    /// <summary>The most matching pages one search reads.</summary>
    private const int MaxPages = 5000;

    /// <summary>A name match counts this much more than a page match: people usually remember names.</summary>
    private const double NameWeight = 2;

    public async Task<SearchResults> SearchAsync(
        Actor actor,
        SearchRequest request,
        CancellationToken ct = default
    )
    {
        var started = Stopwatch.GetTimestamp();
        var query = Merge(SearchQuery.Parse(request.Q), request);
        var live = db.Nodes.VisibleTo(actor).Where(n => n.TrashedAt == null);
        var scope = await FilterAsync(live, query, request.FolderId, actor, ct);
        var terms = query.HighlightTerms;

        List<Candidate> ranked;
        if (query.HasText)
        {
            ranked = await RankAsync(await MatchAsync(scope, query, ct), request.Sort, ct);
        }
        else if (query.HasFilters || request.FolderId is not null)
        {
            // Only filters, such as "type:pdf taken:2024": everything that fits, newest first.
            ranked = (
                await scope
                    .OrderByDescending(n => n.ModifiedAt)
                    .Select(n => n.Id)
                    .Take(MaxCandidates)
                    .ToListAsync(ct)
            )
                .Select(id => new Candidate(id))
                .ToList();
        }
        else
        {
            return new SearchResults([], 0, [], 0, null);
        }

        var page = ranked.Skip(request.Offset).Take(Math.Clamp(request.Limit, 1, 100)).ToList();
        var hits = await HitsAsync(page, terms, ct);
        var pending = await PendingAsync(live, ct);
        int? withoutFilters =
            ranked.Count == 0 && query.HasText && (query.HasFilters || request.FolderId is not null)
                ? (
                    await MatchAsync(
                        live,
                        query with
                        {
                            Type = null,
                            Modified = new(null, null),
                            InFolderNamed = null,
                            Camera = null,
                            Taken = new(null, null),
                            Starred = false,
                        },
                        ct
                    )
                ).Count
                : null;

        DokumentTelemetry.SearchDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
        return new SearchResults(hits, ranked.Count, terms, pending, withoutFilters);
    }

    /// <summary>Name matches as someone types, best first, for the search bar's suggestions.</summary>
    public async Task<List<Node>> SuggestAsync(
        Actor actor,
        string? q,
        int limit,
        CancellationToken ct = default
    )
    {
        var query = SearchQuery.Parse(q);
        if (!query.HasText)
        {
            return [];
        }

        var live = db.Nodes.VisibleTo(actor).Where(n => n.TrashedAt == null);
        var names = await NameHitsAsync(live, query, ct);
        var ids = names.OrderByDescending(n => n.Value).Take(limit).Select(n => n.Key).ToList();
        var nodes = await live.Include(n => n.CurrentVersion)
            .Where(n => ids.Contains(n.Id))
            .ToListAsync(ct);
        return nodes.OrderBy(n => ids.IndexOf(n.Id)).ToList();
    }

    /// <summary>The pages of one file that match, in page order, with boxes for hits in recognized text.</summary>
    public async Task<(IReadOnlyList<string> Terms, List<PageMatch> Pages)> MatchesInAsync(
        Node file,
        string? q,
        CancellationToken ct = default
    )
    {
        var query = SearchQuery.Parse(q);
        if (!query.HasText || file.CurrentVersion is not { } version)
        {
            return (query.HighlightTerms, []);
        }

        // A corrected page matches by its own text, but its boxes are the read page's: the scan still shows the
        // words people didn't change (§13.5).
        var hash = version.BlobHash.ToArray();
        var pages = await db
            .Database.SqlQuery<PageRow>(
                $"""
                SELECT p.page_no AS "PageNo", p.source AS "Source", COALESCE(p.words, r.words)::text AS "Words"
                FROM content_pages p
                LEFT JOIN content_pages r ON p.version_id IS NOT NULL AND r.version_id IS NULL
                    AND r.blob_hash = p.blob_hash AND r.page_no = p.page_no
                WHERE p.id @@@ {BmQuery.Text(
                    query
                )}::jsonb AND p.blob_hash = {hash} AND p.page_no > 0
                  AND (p.version_id = {version.Id}
                       OR (p.version_id IS NULL AND NOT EXISTS (
                           SELECT 1 FROM content_pages e WHERE e.version_id = {version.Id} AND e.page_no = p.page_no)))
                ORDER BY p.page_no
                """
            )
            .ToListAsync(ct);
        var terms = query.HighlightTerms;
        return (
            terms,
            pages
                .Select(p => new PageMatch(
                    p.PageNo,
                    Enum.Parse<PageSource>(p.Source, ignoreCase: true) is var source
                    && source == PageSource.Added
                        ? PageSource.Text
                        : source,
                    p.Words is null ? [] : Boxes(p.Words, terms)
                ))
                .ToList()
        );
    }

    private static SearchQuery Merge(SearchQuery query, SearchRequest request) =>
        query with
        {
            Type = request.Type ?? query.Type,
            Modified = new DateRange(
                request.ModifiedAfter ?? query.Modified.From,
                request.ModifiedBefore ?? query.Modified.To
            ),
            Starred = request.Starred || query.Starred,
        };

    private async Task<IQueryable<Node>> FilterAsync(
        IQueryable<Node> nodes,
        SearchQuery query,
        Guid? folderId,
        Actor actor,
        CancellationToken ct
    )
    {
        if (query.Type == FileType.Folder)
        {
            nodes = nodes.Where(n => n.Kind == NodeKind.Folder);
        }
        else if (query.Type is { } type)
        {
            var patterns = type.MimePatterns.ToArray();
            nodes = nodes.Where(n =>
                n.Kind == NodeKind.File
                && patterns.Any(p => EF.Functions.Like(n.CurrentVersion!.MimeType, p))
            );
        }

        if (query.Modified.From is { } from)
        {
            nodes = nodes.Where(n => n.ModifiedAt >= from);
        }

        if (query.Modified.To is { } to)
        {
            nodes = nodes.Where(n => n.ModifiedAt < to);
        }

        if (query.Starred)
        {
            nodes = nodes.Where(n =>
                db.Stars.Any(s => s.UserId == actor.UserId && s.NodeId == n.Id)
            );
        }

        // Inside a folder: the chip names one; in:"name" means any folder with that name.
        var folders = new List<Node>();
        if (folderId is { } id)
        {
            folders.AddRange(
                await db
                    .Nodes.VisibleTo(actor)
                    .Where(n => n.Id == id && n.TrashedAt == null && n.Kind == NodeKind.Folder)
                    .ToListAsync(ct)
            );
            if (folders.Count == 0)
            {
                throw new DriveException(DriveError.NotFound, "The folder doesn't exist.");
            }
        }

        if (query.InFolderNamed is { } name && NodeName.TryCreate(name, out var folderName, out _))
        {
            var key = folderName!.Key;
            folders.AddRange(
                await db
                    .Nodes.VisibleTo(actor)
                    .Where(n =>
                        n.TrashedAt == null && n.Kind == NodeKind.Folder && n.NameKey == key
                    )
                    .Take(50)
                    .ToListAsync(ct)
            );
            if (folders.Count == 0)
            {
                return nodes.Where(_ => false);
            }
        }

        if (folders.Count > 0)
        {
            var paths = folders.Select(f => f.Path).ToArray();
            var ids = folders.Select(f => f.Id).ToArray();
            var below = db.Nodes.FromSql($"SELECT * FROM nodes WHERE path <@ {paths}::ltree[]");
            nodes = nodes.Where(n => below.Any(b => b.Id == n.Id) && !ids.Contains(n.Id));
        }

        if (!query.Taken.IsEmpty)
        {
            // An edited date wins over the camera's (§13.5); a partial one counts from its first day.
            var editedTaken = db.VersionDetails.Where(d => d.TakenAt != null);
            var editedInRange = editedTaken;
            var readInRange = db.BlobMetadata.AsQueryable();
            if (query.Taken.From is { } takenFrom)
            {
                editedInRange = editedInRange.Where(d => d.TakenAt >= takenFrom);
                readInRange = readInRange.Where(m => m.TakenAt >= takenFrom);
            }

            if (query.Taken.To is { } takenTo)
            {
                editedInRange = editedInRange.Where(d => d.TakenAt < takenTo);
                readInRange = readInRange.Where(m => m.TakenAt < takenTo);
            }

            nodes = nodes.Where(n =>
                n.CurrentVersion != null
                && (
                    editedInRange.Any(d => d.VersionId == n.CurrentVersionId)
                    || (
                        !editedTaken.Any(d => d.VersionId == n.CurrentVersionId)
                        && readInRange.Any(m => m.BlobHash == n.CurrentVersion.BlobHash)
                    )
                )
            );
        }

        if (query.Camera is { } camera)
        {
            var pattern =
                "%" + camera.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            nodes = nodes.Where(n =>
                n.CurrentVersion != null
                && db.BlobMetadata.Any(m =>
                    m.BlobHash == n.CurrentVersion.BlobHash
                    && EF.Functions.ILike(m.Camera!, pattern)
                )
            );
        }

        return nodes;
    }

    /// <summary>Every node in <paramref name="scope"/> whose name or text matches, with its scores and best page.</summary>
    private async Task<Dictionary<Guid, Candidate>> MatchAsync(
        IQueryable<Node> scope,
        SearchQuery query,
        CancellationToken ct
    )
    {
        var candidates = (await NameHitsAsync(scope, query, ct)).ToDictionary(
            n => n.Key,
            n => new Candidate(n.Key) { NameScore = n.Value }
        );

        var pages = await db
            .Database.SqlQuery<PageHit>(
                $"""
                SELECT n.id AS "NodeId", p.id AS "PageId", p.page_no AS "PageNo", p.source AS "Source", p.score AS "Score"
                FROM (SELECT id, blob_hash, version_id, page_no, source, pdb.score(id) AS score FROM content_pages WHERE id @@@ {BmQuery.Text(
                    query
                )}::jsonb) p
                JOIN file_versions v ON v.blob_hash = p.blob_hash AND (p.version_id IS NULL OR p.version_id = v.id)
                JOIN nodes n ON n.current_version_id = v.id
                WHERE p.version_id IS NOT NULL OR p.page_no = 0
                   OR NOT EXISTS (SELECT 1 FROM content_pages e WHERE e.version_id = v.id AND e.page_no = p.page_no)
                """
            )
            .Join(scope, p => p.NodeId, n => n.Id, (p, _) => p)
            .OrderByDescending(p => p.Score)
            .Take(MaxPages)
            .ToListAsync(ct);
        foreach (var page in pages)
        {
            var candidate = candidates.TryGetValue(page.NodeId, out var found)
                ? found
                : candidates[page.NodeId] = new Candidate(page.NodeId);
            if (page.PageNo > 0)
            {
                candidate.PagesWithHits++;
            }

            if (candidate.Page is null || page.Score > candidate.Page.Score)
            {
                candidate.Page = page;
            }
        }

        return candidates;
    }

    private async Task<Dictionary<Guid, double>> NameHitsAsync(
        IQueryable<Node> scope,
        SearchQuery query,
        CancellationToken ct
    ) =>
        await db
            .Database.SqlQuery<ScoredId>(
                $"""SELECT id AS "Id", pdb.score(id) AS "Score" FROM nodes WHERE id @@@ {BmQuery.Name(query)}::jsonb"""
            )
            .Join(scope, h => h.Id, n => n.Id, (h, _) => h)
            .OrderByDescending(h => h.Score)
            .Take(MaxCandidates)
            .ToDictionaryAsync(h => h.Id, h => (double)h.Score, ct);

    private async Task<List<Candidate>> RankAsync(
        Dictionary<Guid, Candidate> candidates,
        SearchSort sort,
        CancellationToken ct
    )
    {
        if (sort == SearchSort.Relevance)
        {
            return [.. candidates.Values.OrderByDescending(c => c.Score)];
        }

        var ids = candidates.Keys.ToList();
        var modified = await db
            .Nodes.Where(n => ids.Contains(n.Id))
            .Select(n => new { n.Id, n.ModifiedAt })
            .ToListAsync(ct);
        return [.. modified.OrderByDescending(m => m.ModifiedAt).Select(m => candidates[m.Id])];
    }

    private async Task<List<SearchHit>> HitsAsync(
        List<Candidate> page,
        IReadOnlyList<string> terms,
        CancellationToken ct
    )
    {
        var ids = page.Select(c => c.NodeId).ToList();
        var nodes = await db
            .Nodes.Include(n => n.CurrentVersion)
            .Where(n => ids.Contains(n.Id))
            .ToDictionaryAsync(n => n.Id, ct);
        var pageIds = page.Where(c => c.Page is not null).Select(c => c.Page!.PageId).ToList();
        var texts = await db
            .ContentPages.Where(p => pageIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Text, ct);
        var hashes = nodes
            .Values.Select(n => n.CurrentVersion?.BlobHash)
            .OfType<ContentHash>()
            .Distinct()
            .ToList();
        var pageCounts = await db
            .BlobContents.Where(c => hashes.Contains(c.BlobHash) && c.PageCount != null)
            .ToDictionaryAsync(c => c.BlobHash, c => c.PageCount, ct);

        return page.Where(c => nodes.ContainsKey(c.NodeId))
            .Select(c =>
            {
                var node = nodes[c.NodeId];
                var best = c.Page;
                var count = node.CurrentVersion is { } v
                    ? pageCounts.GetValueOrDefault(v.BlobHash)
                    : null;
                return new SearchHit(
                    node,
                    best is null ? MatchSource.Name
                        : best.Source == "added" ? MatchSource.Text
                        : Enum.Parse<MatchSource>(best.Source, ignoreCase: true),
                    best is { PageNo: > 0 } && count is not null ? best.PageNo : null,
                    count,
                    c.PagesWithHits,
                    best is not null && texts.TryGetValue(best.PageId, out var text)
                        ? Highlighter.Snip(text, terms)
                        : null
                );
            })
            .ToList();
    }

    /// <summary>Files in scope whose current bytes still have a text or metadata job waiting or running.</summary>
    private async Task<int> PendingAsync(IQueryable<Node> live, CancellationToken ct)
    {
        var waiting = db.Database.SqlQuery<Guid>(
            $"""
            SELECT n.id AS "Value" FROM jobs j
            JOIN file_versions v ON encode(v.blob_hash, 'hex') = j.subject
            JOIN nodes n ON n.current_version_id = v.id
            WHERE j.type IN ({JobType.Text.Name}, {JobType.Metadata.Name}) AND j.status IN ('queued', 'running')
            """
        );
        return await live.Where(n => waiting.Contains(n.Id)).CountAsync(ct);
    }

    private static List<double[]> Boxes(string wordsJson, IReadOnlyList<string> terms)
    {
        var boxes = new List<double[]>();
        using var words = System.Text.Json.JsonDocument.Parse(wordsJson);
        foreach (var word in words.RootElement.EnumerateArray())
        {
            var text = word[4].GetString() ?? "";
            if (Highlighter.Hits(text, terms).Count > 0)
            {
                boxes.Add([
                    word[0].GetDouble(),
                    word[1].GetDouble(),
                    word[2].GetDouble(),
                    word[3].GetDouble(),
                ]);
            }
        }

        return boxes;
    }

    private sealed record ScoredId(Guid Id, float Score);

    private sealed record PageHit(Guid NodeId, long PageId, int PageNo, string Source, float Score);

    private sealed record PageRow(int PageNo, string Source, string? Words);

    private sealed class Candidate(Guid nodeId)
    {
        public Guid NodeId { get; } = nodeId;
        public double NameScore { get; init; }
        public PageHit? Page { get; set; }
        public int PagesWithHits { get; set; }
        public double Score => (NameScore * NameWeight) + (Page?.Score ?? 0);
    }
}
