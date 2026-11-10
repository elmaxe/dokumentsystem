import type { FileDetailsDto, FileTags, FileTextDto, TagSuggestion, TextPage } from "@dokument/api";
import { keepPreviousData, queryOptions, type QueryClient } from "@tanstack/react-query";
import { api, send, unwrap } from "./api";

/** What a file says about itself, with people's edits on top (docs/design.md §13.5). */
export const fileDetailsQuery = (id: string, versionId: string | null) =>
  queryOptions({
    queryKey: ["fileDetails", id, versionId],
    queryFn: (): Promise<FileDetailsDto> =>
      unwrap(api().GET("/api/v1/nodes/{id}/details", { params: { path: { id } } })),
    // Asked again while the worker is still reading them.
    refetchInterval: (query) => (query.state.data?.reading ? 5_000 : false),
  });

/** `limit` pages of a file's text from page `from` (§11). */
export const fileTextPage = (id: string, from: number, limit: number): Promise<FileTextDto> =>
  unwrap(api().GET("/api/v1/nodes/{id}/text", { params: { path: { id }, query: { from, limit } } }));

/** The start of a file's text, for the Info screen. */
export const fileTextQuery = (id: string, versionId: string | null) =>
  queryOptions({
    queryKey: ["fileText", id, versionId, "start"],
    queryFn: () => fileTextPage(id, 1, 2),
    refetchInterval: (query) => (query.state.data?.state === "reading" ? 5_000 : false),
  });

/** All of a file's text, page by page, for Copy all text. */
export async function allText(id: string): Promise<string> {
  const pages: TextPage[] = [];
  let next: number | null = 1;
  while (next != null) {
    const more: FileTextDto = await fileTextPage(id, next, 100);
    pages.push(...more.pages);
    next = more.nextFrom ?? null;
  }
  return pages.map((page) => page.text).join("\n\n");
}

/** A file's tags and the automatic ones someone took off it (§13.4). */
export const fileTagsQuery = (id: string) =>
  queryOptions({
    queryKey: ["tags", "file", id],
    queryFn: (): Promise<FileTags> => unwrap(api().GET("/api/v1/nodes/{id}/tags", { params: { path: { id } } })),
  });

/** Tags on files the person can see that start with what's typed, most used first. */
export const tagSuggestionsQuery = (q: string) =>
  queryOptions({
    queryKey: ["tags", "suggest", q, 8],
    queryFn: async (): Promise<TagSuggestion[]> =>
      (await unwrap(api().GET("/api/v1/tags", { params: { query: { q: q || undefined, limit: 8 } } }))).tags,
    placeholderData: keepPreviousData,
    staleTime: 30_000,
  });

export const changeTags = (ids: string[], change: { add?: string[]; remove?: string[] }) =>
  send(api().POST("/api/v1/nodes/tags", { body: { ids, add: change.add ?? [], remove: change.remove ?? [] } }));

/** Refetches what shows tags: listings, the open file, its tags, search results and suggestions. */
export const invalidateTags = (queryClient: QueryClient) =>
  Promise.all(
    ["folder", "node", "tags", "search", "starred", "recent"].map((key) =>
      queryClient.invalidateQueries({ queryKey: [key] }),
    ),
  );
