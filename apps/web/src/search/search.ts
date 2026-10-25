import type { MatchesResponse, NodeDto, SearchResponse, SearchSort, SuggestResponse } from "@dokument/api";
import { keepPreviousData, queryOptions } from "@tanstack/react-query";
import { api } from "../api";
import { unwrap } from "../files/drive";

/** The file types the Type chip offers, as the server names them (docs/design.md §13.3). */
export const searchTypes = ["pdf", "doc", "sheet", "slides", "image", "video", "audio", "text", "archive", "folder"] as const;
export type SearchType = (typeof searchTypes)[number];

export const modifiedRanges = ["today", "week", "month", "year", "lastYear"] as const;
export type ModifiedRange = (typeof modifiedRanges)[number];

/** The search page's URL: the query and the filter chips, so a search can be bookmarked and shared. */
export type SearchParams = {
  q?: string;
  type?: SearchType;
  modified?: ModifiedRange;
  folder?: string;
  starred?: boolean;
  sort?: SearchSort;
};

export function validateSearchParams(search: Record<string, unknown>): SearchParams {
  const pick = <T extends string>(value: unknown, allowed: readonly T[]) =>
    allowed.includes(value as T) ? (value as T) : undefined;
  return {
    q: typeof search.q === "string" && search.q ? search.q : undefined,
    type: pick(search.type, searchTypes),
    modified: pick(search.modified, modifiedRanges),
    folder: typeof search.folder === "string" && search.folder ? search.folder : undefined,
    starred: search.starred === true || search.starred === "true" ? true : undefined,
    sort: pick(search.sort, ["relevance", "modified"] as const),
  };
}

export const hasFilters = (params: SearchParams) => !!(params.type || params.modified || params.folder || params.starred);

/** A Modified chip as dates, in the browser's time zone: "Today" starts at local midnight. */
function modifiedBounds(range: ModifiedRange | undefined, now = new Date()): { after?: string; before?: string } {
  if (!range) return {};
  const midnight = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  const daysAgo = (days: number) => new Date(midnight.getTime() - days * 86_400_000).toISOString();
  switch (range) {
    case "today":
      return { after: midnight.toISOString() };
    case "week":
      return { after: daysAgo(6) };
    case "month":
      return { after: daysAgo(29) };
    case "year":
      return { after: new Date(now.getFullYear(), 0, 1).toISOString() };
    case "lastYear":
      return {
        after: new Date(now.getFullYear() - 1, 0, 1).toISOString(),
        before: new Date(now.getFullYear(), 0, 1).toISOString(),
      };
  }
}

/**
 * Results for the search page. While the worker is still reading text in files that might match, the results are
 * polled so those files appear on their own: every 3 s at first, then every 15 s.
 */
export const searchQuery = (params: SearchParams, limit: number) =>
  queryOptions({
    queryKey: ["search", params, limit],
    queryFn: (): Promise<SearchResponse> => {
      const { after, before } = modifiedBounds(params.modified);
      return unwrap(
        api.GET("/api/v1/search", {
          params: {
            query: {
              q: params.q,
              type: params.type,
              modifiedAfter: after,
              modifiedBefore: before,
              folderId: params.folder,
              starred: params.starred,
              sort: params.sort,
              limit,
            },
          },
        }),
      );
    },
    enabled: !!params.q || hasFilters(params),
    placeholderData: keepPreviousData,
    refetchInterval: (query) =>
      (query.state.data?.pending ?? 0) > 0 ? (query.state.dataUpdateCount < 20 ? 3_000 : 15_000) : false,
  });

export const suggestQuery = (q: string) =>
  queryOptions({
    queryKey: ["search-suggest", q],
    queryFn: (): Promise<SuggestResponse> => unwrap(api.GET("/api/v1/search/suggest", { params: { query: { q } } })),
    enabled: q.trim().length >= 2,
    placeholderData: keepPreviousData,
    staleTime: 30_000,
  });

export const matchesQuery = (fileId: string, q: string) =>
  queryOptions({
    queryKey: ["search-matches", fileId, q],
    queryFn: (): Promise<MatchesResponse> =>
      unwrap(api.GET("/api/v1/nodes/{id}/matches", { params: { path: { id: fileId }, query: { q } } })),
    enabled: !!q,
    staleTime: 60_000,
  });

/**
 * The results the person last opened a file from, so the viewer can step through them and go back. Kept for the
 * page's lifetime only; search queries are never stored on the server (§13.3).
 */
let lastResults: { params: SearchParams; nodes: NodeDto[] } | null = null;

export const rememberResults = (params: SearchParams, nodes: NodeDto[]) => {
  lastResults = { params, nodes };
};

export const lastSearchResults = () => lastResults;
