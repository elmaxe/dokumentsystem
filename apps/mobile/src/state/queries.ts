import type {
  CurrentSession,
  FolderListing,
  NodeDetails,
  NodeDto,
  Person,
  RecentListing,
  RestoredItem,
  ServerInfo,
  SessionInfo,
  StarredListing,
  StorageUsage,
  TrashListing,
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
export const pollWhilePending = (pending: boolean, updates: number) => (pending ? (updates < 30 ? 2_000 : 10_000) : false);

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

export const trashQuery = queryOptions({
  queryKey: ["trash"],
  queryFn: (): Promise<TrashListing> => unwrap(api().GET("/api/v1/trash")),
});

/** Refetches the folders that changed, and the places that show their items, after a change in them. */
export async function invalidateFolders(queryClient: QueryClient, folderIds: (string | null)[]) {
  await Promise.all([
    ...Array.from(new Set(folderIds.map((id) => id ?? "root")), (key) =>
      queryClient.invalidateQueries({ queryKey: ["folder", key] }),
    ),
    ...["node", "storage", "starred", "recent"].map((key) => queryClient.invalidateQueries({ queryKey: [key] })),
  ]);
}

/** Refetches everything an item going into or out of the trash shows up in. */
export async function invalidateTrash(queryClient: QueryClient) {
  await Promise.all(
    ["trash", "folder", "node", "storage", "starred", "recent"].map((key) =>
      queryClient.invalidateQueries({ queryKey: [key] }),
    ),
  );
}

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
  createFolder: (folderId: string | null, name: string): Promise<NodeDto> =>
    unwrap(api().POST("/api/v1/folders", { body: { folderId, name } })),
  rename: (id: string, name: string): Promise<NodeDto> =>
    unwrap(api().POST("/api/v1/nodes/{id}/rename", { params: { path: { id } }, body: { name } })),
  move: (id: string, folderId: string | null): Promise<NodeDto> =>
    unwrap(api().POST("/api/v1/nodes/{id}/move", { params: { path: { id } }, body: { folderId } })),
  /** Who would stop seeing these items if they moved to the folder (§9). */
  losingAccess: async (ids: string[], folderId: string | null): Promise<Person[]> =>
    (await unwrap(api().POST("/api/v1/nodes/move-check", { body: { ids, folderId } }))).losingAccess,
  trash: (ids: string[]) => send(api().POST("/api/v1/nodes/trash", { body: { ids } })),
  restore: async (ids: string[]): Promise<RestoredItem[]> =>
    (await unwrap(api().POST("/api/v1/trash/restore", { body: { ids } }))).items,
  deleteForever: (ids: string[]) => send(api().POST("/api/v1/trash/delete", { body: { ids } })),
  emptyTrash: async () => (await unwrap(api().POST("/api/v1/trash/empty"))).items,
  endSession: (id: string) => send(api().DELETE("/api/v1/sessions/{id}", { params: { path: { id } } })),
  /** Ends this session; answers the IdP's end-session URL, or null when the IdP has none (§5). */
  signOut: () => unwrap(api().POST("/api/v1/auth/logout")).then((result) => result.endSessionUrl ?? null),
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
