import type {
  CurrentSession,
  FolderListing,
  NodeDetails,
  NodeDto,
  RecentListing,
  ServerInfo,
  SessionInfo,
  StarredListing,
  StorageUsage,
} from "@dokument/api";
import { thumbnailUrl } from "@dokument/api";
import { formatDuration, kindOf, type FileKind } from "@dokument/core";
import { queryOptions, type QueryClient } from "@tanstack/react-query";
import { api, authHeaders, baseUrl, send, unwrap } from "./api";

// The query keys match the web app's, so the same live notices refetch the same data (docs/design.md §15.4).

export const serverInfoQuery = queryOptions({
  queryKey: ["server", "info"],
  queryFn: (): Promise<ServerInfo> => unwrap(api().GET("/api/v1/server/info")),
});

export const sessionQuery = queryOptions({
  queryKey: ["auth", "session"],
  queryFn: (): Promise<CurrentSession> => unwrap(api().GET("/api/v1/auth/session")),
  staleTime: 5 * 60_000,
});

export const sessionsQuery = queryOptions({
  queryKey: ["sessions"],
  queryFn: (): Promise<SessionInfo[]> => unwrap(api().GET("/api/v1/sessions")),
});

/** Polled every 2 s while the worker still has something to do for a file on screen, then every 10 s. */
const pollWhilePending = (pending: boolean, updates: number) => (pending ? (updates < 30 ? 2_000 : 10_000) : false);

const busy = (node: NodeDto) => node.processing.length > 0 || node.thumbnail === "pending";

export const folderQuery = (folderId: string | null) =>
  queryOptions({
    queryKey: ["folder", folderId ?? "root"],
    queryFn: (): Promise<FolderListing> =>
      unwrap(api().GET("/api/v1/nodes", { params: { query: folderId ? { folderId } : {} } })),
    refetchInterval: (query) =>
      pollWhilePending(query.state.data?.items.some(busy) ?? false, query.state.dataUpdateCount),
  });

export const nodeQuery = (id: string) =>
  queryOptions({
    queryKey: ["node", id],
    queryFn: (): Promise<NodeDetails> => unwrap(api().GET("/api/v1/nodes/{id}", { params: { path: { id } } })),
  });

export const storageQuery = queryOptions({
  queryKey: ["storage"],
  queryFn: (): Promise<StorageUsage> => unwrap(api().GET("/api/v1/storage")),
});

export const starredQuery = queryOptions({
  queryKey: ["starred"],
  queryFn: (): Promise<StarredListing> => unwrap(api().GET("/api/v1/starred")),
});

export const recentQuery = queryOptions({
  queryKey: ["recent"],
  queryFn: (): Promise<RecentListing> => unwrap(api().GET("/api/v1/recent")),
});

/** Refetches what shows stars: every listing, the open file, and Starred itself. */
export async function invalidateStars(queryClient: QueryClient) {
  await Promise.all(
    ["folder", "node", "starred", "recent"].map((key) => queryClient.invalidateQueries({ queryKey: [key] })),
  );
}

export const drive = {
  star: (ids: string[], starred: boolean) =>
    send(api().POST(starred ? "/api/v1/nodes/star" : "/api/v1/nodes/unstar", { body: { ids } })),
  /** Tells the server the person opened a file, for their Recent page. Not audited (§18). */
  opened: (id: string) => send(api().POST("/api/v1/nodes/{id}/opened", { params: { path: { id } } })),
  /** Asks for a playable copy now, ahead of background work. Refused when copies are turned off (§14). */
  requestPlayable: (id: string) => unwrap(api().POST("/api/v1/nodes/{id}/playable", { params: { path: { id } } })),
  endSession: (id: string) => send(api().DELETE("/api/v1/sessions/{id}", { params: { path: { id } } })),
  signOut: () => send(api().POST("/api/v1/auth/logout")),
};

export const kindOfNode = (node: NodeDto): FileKind => (node.kind === "folder" ? "folder" : kindOf(node.name, node.mimeType));

export const durationOf = (node: NodeDto) =>
  node.media?.durationSeconds != null ? formatDuration(node.media.durationSeconds) : undefined;

/** A thumbnail as expo-image loads it, with the session, or null while there is none (§12). */
export function thumbnailSource(node: NodeDto, size: "small" | "medium" = "medium") {
  if (node.thumbnail !== "ready") return null;
  return {
    uri: thumbnailUrl(node.id, node.currentVersionId, size, baseUrl()),
    headers: authHeaders(),
    // The URL names the version, so a new version is a new cache entry and an old one can be kept.
    cacheKey: `${node.id}/${node.currentVersionId ?? ""}/${size}`,
  };
}
