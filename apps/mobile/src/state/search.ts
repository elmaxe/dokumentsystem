import type { MatchesResponse, SearchResponse, SearchSort, SuggestResponse, TagSuggestion } from "@dokument/api";
import { keepPreviousData, queryOptions } from "@tanstack/react-query";
import { api, unwrap } from "./api";

// Search (docs/design.md §13.3), the same requests and query keys as the web app. Searches are never stored.

/** The file types the Type chip offers, as the server names them. */
export const searchTypes = ["pdf", "doc", "sheet", "slides", "image", "video", "audio", "text", "archive", "folder"] as const;
export type SearchType = (typeof searchTypes)[number];

export const modifiedRanges = ["today", "week", "month", "year", "lastYear"] as const;
export type ModifiedRange = (typeof modifiedRanges)[number];

/** A search: the words and the filter chips. */
export type SearchParams = {
  q?: string;
  type?: SearchType;
  modified?: ModifiedRange;
  folder?: string;
  starred?: boolean;
  tag?: string;
  sort?: SearchSort;
};

export const hasFilters = (params: SearchParams) =>
  !!(params.type || params.modified || params.folder || params.starred || params.tag);

/** A Modified chip as dates, in the phone's time zone: "Today" starts at local midnight. */
export function modifiedBounds(range: ModifiedRange | undefined, now = new Date()): { after?: string; before?: string } {
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
 * Results for a search. While the worker is still reading text in files that might match, they're fetched again so
 * those files appear by themselves: every 3 s at first, then every 15 s.
 */
export const searchQuery = (params: SearchParams, limit: number) =>
  queryOptions({
    queryKey: ["search", params, limit],
    queryFn: (): Promise<SearchResponse> => {
      const { after, before } = modifiedBounds(params.modified);
      return unwrap(
        api().GET("/api/v1/search", {
          params: {
            query: {
              q: params.q,
              type: params.type,
              modifiedAfter: after,
              modifiedBefore: before,
              folderId: params.folder,
              starred: params.starred,
              tag: params.tag ? [params.tag] : undefined,
              sort: params.sort,
              limit,
            },
          },
        }),
      );
    },
    enabled: !!params.q || hasFilters(params),
    placeholderData: keepPreviousData,
    // Not kept long, and never saved on the phone (only listings are, queryClient.ts).
    gcTime: 60_000,
    refetchInterval: (query) =>
      (query.state.data?.pending ?? 0) > 0 ? (query.state.dataUpdateCount < 20 ? 3_000 : 15_000) : false,
  });

export const suggestQuery = (q: string) =>
  queryOptions({
    queryKey: ["search-suggest", q],
    queryFn: (): Promise<SuggestResponse> => unwrap(api().GET("/api/v1/search/suggest", { params: { query: { q } } })),
    enabled: q.trim().length >= 2,
    placeholderData: keepPreviousData,
    staleTime: 30_000,
    gcTime: 60_000,
  });

/** A file's pages that match, with the recognized boxes for scans and photos, or the times in a recording. */
export const matchesQuery = (fileId: string, q: string) =>
  queryOptions({
    queryKey: ["search-matches", fileId, q],
    queryFn: (): Promise<MatchesResponse> =>
      unwrap(api().GET("/api/v1/nodes/{id}/matches", { params: { path: { id: fileId }, query: { q } } })),
    enabled: !!q,
    staleTime: 60_000,
    gcTime: 60_000,
  });

/** The person's tags, most used first, for the Tag chip. */
export const tagsQuery = queryOptions({
  queryKey: ["tags", "suggest", "", 30],
  queryFn: async (): Promise<TagSuggestion[]> =>
    (await unwrap(api().GET("/api/v1/tags", { params: { query: { limit: 30 } } }))).tags,
  staleTime: 30_000,
});
