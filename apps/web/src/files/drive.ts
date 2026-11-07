import type {
  DuplicateCheckResult,
  FileTags,
  FileDetailsDto,
  FileProcessing,
  FileTextDto,
  RecognizedWordsDto,
  FolderConflictPolicy,
  FolderListing,
  FolderTreeEntry,
  NameConflictPolicy,
  NodeAccess,
  NodeDetails,
  NodeDto,
  Person,
  Problem,
  RecentListing,
  RestoredItem,
  SharedItem,
  ShareRole,
  StarredListing,
  StorageUsage,
  TagSuggestion,
  Transcript,
  TrashListing,
  VersionDto,
} from "@dokument/api";
import { thumbnailUrl } from "@dokument/api";
import { formatDuration } from "@dokument/core";
import { kindOf, type FileItem } from "@dokument/ui";
import { keepPreviousData, queryOptions, type QueryClient } from "@tanstack/react-query";
import type { useTranslation } from "react-i18next";
import { api } from "../api";
import { isLiveConnected } from "../live";

type TFunction = ReturnType<typeof useTranslation>["t"];

/** A refused API request, with the server's problem details. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly problem: Problem = {},
  ) {
    super(problem.detail ?? problem.title ?? `Request failed with ${status}`);
  }
}

/** For requests that answer 204 No Content. */
export async function send(request: Promise<{ error?: unknown; response: Response }>): Promise<void> {
  const { error, response } = await request;
  if (!response.ok) throw new ApiError(response.status, (error ?? {}) as Problem);
}

export async function unwrap<T>(request: Promise<{ data?: T; error?: unknown; response: Response }>): Promise<T> {
  const { data, error, response } = await request;
  if (!response.ok || data === undefined) throw new ApiError(response.status, (error ?? {}) as Problem);
  return data;
}

/** What to tell the user when a file operation fails, in their language. */
export function errorMessage(t: TFunction, error: unknown): string {
  const problem = error instanceof ApiError ? error.problem : undefined;
  switch (problem?.code) {
    case "nameTaken":
      return t("errors.nameTaken");
    case "notFound":
      return t("errors.notFound");
    case "quotaExceeded":
      return t("errors.quotaExceeded");
    case "forbidden":
      return t("errors.forbidden");
    case "uploadMismatch":
      return t("errors.changedWhileUploading");
    case "invalidRequest":
      return problem.detail?.includes("into itself") ? t("errors.moveIntoItself") : t("errors.invalidName");
    default:
      return t("errors.generic");
  }
}

/** A node as the file views show it. */
export type Item = FileItem & { node: NodeDto };

export const toItem = (node: NodeDto): Item => ({
  id: node.id,
  name: node.name,
  kind: node.kind === "folder" ? "folder" : kindOf(node.name, node.mimeType),
  size: node.size ?? undefined,
  contents: node.contents ?? undefined,
  modified: node.modifiedAt,
  owner: node.ownerName,
  thumbnail: node.thumbnail === "ready" ? thumbnailUrl(node.id, node.currentVersionId) : undefined,
  thumbnailPending: node.thumbnail === "pending",
  starred: node.starred,
  shared: node.shared,
  tags: node.tags.map((tag) => ({ name: tag.name, source: tag.source })),
  processing: node.processing,
  duration: node.media?.durationSeconds != null ? formatDuration(node.media.durationSeconds) : undefined,
  node,
});

/**
 * Whether a node's listing should be polled because the worker still has something to do for it (§10.1, §16.4).
 * A finished thumbnail arrives as a live notice, so it's polled for only without a live connection; the other
 * steps don't send notices yet, so they're always polled for.
 */
const busy = (node: NodeDto) =>
  node.processing.some((s) => s.step !== "thumbnail") || (node.thumbnail === "pending" && !isLiveConnected());

/** Polled while the worker is busy with something on screen: every 2 s at first, then every 10 s for slow ones. */
const pollWhilePending = (pending: boolean, updates: number) => (pending ? (updates < 30 ? 2_000 : 10_000) : false);

export const folderQuery = (folderId: string | null) =>
  queryOptions({
    queryKey: ["folder", folderId ?? "root"],
    queryFn: (): Promise<FolderListing> =>
      unwrap(api.GET("/api/v1/nodes", { params: { query: folderId ? { folderId } : {} } })),
    refetchInterval: (query) =>
      pollWhilePending(query.state.data?.items.some(busy) ?? false, query.state.dataUpdateCount),
  });

export const nodeQuery = (id: string) =>
  queryOptions({
    queryKey: ["node", id],
    queryFn: (): Promise<NodeDetails> => unwrap(api.GET("/api/v1/nodes/{id}", { params: { path: { id } } })),
    refetchInterval: (query) =>
      // A playable copy's progress has no live notice, so the viewer asks every 3 s while one is on its way (§14).
      copyOnItsWay(query.state.data?.node)
        ? 3_000
        : pollWhilePending(!!query.state.data && busy(query.state.data.node), query.state.dataUpdateCount),
  });

const copyOnItsWay = (node: NodeDto | undefined) => node?.media?.copy === "waiting" || node?.media?.copy === "making";

/** A file's processing steps for the details panel, polled while a step is still to do (only thumbnails send notices). */
export const processingQuery = (id: string, versionId: string | null) =>
  queryOptions({
    queryKey: ["processing", id, versionId],
    queryFn: (): Promise<FileProcessing> =>
      unwrap(api.GET("/api/v1/nodes/{id}/processing", { params: { path: { id } } })),
    refetchInterval: (query) =>
      pollWhilePending(
        query.state.data?.steps.some((s) => ["waiting", "paused", "running"].includes(s.state)) ?? false,
        query.state.dataUpdateCount,
      ),
  });

/** What the file says about itself, with edits (§13.5), polled while the worker is still reading it. */
export const fileDetailsQuery = (id: string, versionId: string | null) =>
  queryOptions({
    queryKey: ["fileDetails", id, versionId],
    queryFn: (): Promise<FileDetailsDto> =>
      unwrap(api.GET("/api/v1/nodes/{id}/details", { params: { path: { id } } })),
    refetchInterval: (query) => pollWhilePending(query.state.data?.reading ?? false, query.state.dataUpdateCount),
  });

/** The start of the file's text for its info: enough pages to say where the text came from. */
export const fileTextQuery = (id: string, versionId: string | null) =>
  queryOptions({
    queryKey: ["fileText", id, versionId, "start"],
    queryFn: () => fileTextPage(id, 1, 5),
    refetchInterval: (query) =>
      pollWhilePending(query.state.data?.state === "reading", query.state.dataUpdateCount),
  });

export const fileTextPage = (id: string, from: number, limit = 20): Promise<FileTextDto> =>
  unwrap(api.GET("/api/v1/nodes/{id}/text", { params: { path: { id }, query: { from, limit } } }));

/** The words recognized on a page of a scan or photo, with their boxes, for the viewer's Text toggle (§14). */
export const wordsQuery = (id: string, versionId: string | null, page: number) =>
  queryOptions({
    queryKey: ["words", id, versionId, page],
    queryFn: (): Promise<RecognizedWordsDto> =>
      unwrap(api.GET("/api/v1/nodes/{id}/words", { params: { path: { id }, query: { page } } })),
    staleTime: 5 * 60_000,
  });

export const versionsQuery = (id: string) =>
  queryOptions({
    queryKey: ["versions", id],
    queryFn: (): Promise<VersionDto[]> =>
      unwrap(api.GET("/api/v1/nodes/{id}/versions", { params: { path: { id } } })),
  });

/** A file's tags and the automatic ones someone took off it (§13.4). */
export const fileTagsQuery = (id: string) =>
  queryOptions({
    queryKey: ["tags", "file", id],
    queryFn: (): Promise<FileTags> => unwrap(api.GET("/api/v1/nodes/{id}/tags", { params: { path: { id } } })),
  });

/** Tags on files the person can see that start with what's typed, most used first. */
export const tagSuggestionsQuery = (q: string, limit = 8) =>
  queryOptions({
    queryKey: ["tags", "suggest", q, limit],
    queryFn: async (): Promise<TagSuggestion[]> =>
      (await unwrap(api.GET("/api/v1/tags", { params: { query: { q: q || undefined, limit } } }))).tags,
    placeholderData: keepPreviousData,
    staleTime: 30_000,
  });

export const storageQuery = queryOptions({
  queryKey: ["storage"],
  queryFn: (): Promise<StorageUsage> => unwrap(api.GET("/api/v1/storage")),
});

export const starredQuery = queryOptions({
  queryKey: ["starred"],
  queryFn: (): Promise<StarredListing> => unwrap(api.GET("/api/v1/starred")),
});

export const recentQuery = queryOptions({
  queryKey: ["recent"],
  queryFn: (): Promise<RecentListing> => unwrap(api.GET("/api/v1/recent")),
});

/** The top-most things shared with the person, newest first (§9). */
export const sharedQuery = queryOptions({
  queryKey: ["shared"],
  queryFn: (): Promise<SharedItem[]> => unwrap(api.GET("/api/v1/shared-with-me")),
});

/** Who can see a node and what the person asking may do with it (§9). */
export const accessQuery = (id: string) =>
  queryOptions({
    queryKey: ["access", id],
    queryFn: (): Promise<NodeAccess> => unwrap(api.GET("/api/v1/nodes/{id}/access", { params: { path: { id } } })),
  });

/** People to share with, by name or email. */
export const peopleQuery = (q: string) =>
  queryOptions({
    queryKey: ["people", q],
    queryFn: (): Promise<Person[]> => unwrap(api.GET("/api/v1/people", { params: { query: { q: q || undefined } } })),
    placeholderData: keepPreviousData,
    staleTime: 60_000,
  });

export const trashQuery = queryOptions({
  queryKey: ["trash"],
  queryFn: (): Promise<TrashListing> => unwrap(api.GET("/api/v1/trash")),
});

/** Refetches what a change to files in these folders can affect. */
export async function invalidateFolders(queryClient: QueryClient, folderIds: (string | null)[]) {
  await Promise.all([
    ...Array.from(new Set(folderIds.map((id) => id ?? "root")), (key) =>
      queryClient.invalidateQueries({ queryKey: ["folder", key] }),
    ),
    queryClient.invalidateQueries({ queryKey: ["storage"] }),
    queryClient.invalidateQueries({ queryKey: ["starred"] }),
    queryClient.invalidateQueries({ queryKey: ["recent"] }),
  ]);
}

/** Refetches what shows stars: every listing, the open file, and Starred itself. */
export async function invalidateStars(queryClient: QueryClient) {
  await Promise.all(
    ["folder", "node", "starred", "recent"].map((key) => queryClient.invalidateQueries({ queryKey: [key] })),
  );
}

/** Refetches what shows tags: every listing, the open file, its tags, search results and suggestions. */
export async function invalidateTags(queryClient: QueryClient) {
  await Promise.all(
    ["folder", "node", "tags", "search", "starred", "recent"].map((key) =>
      queryClient.invalidateQueries({ queryKey: [key] }),
    ),
  );
}

/** Refetches after moving things in or out of the trash: the trash, the storage meter and every folder listing. */
export async function invalidateTrash(queryClient: QueryClient) {
  await Promise.all([
    queryClient.invalidateQueries({ queryKey: ["trash"] }),
    queryClient.invalidateQueries({ queryKey: ["folder"] }),
    queryClient.invalidateQueries({ queryKey: ["storage"] }),
    queryClient.invalidateQueries({ queryKey: ["starred"] }),
    queryClient.invalidateQueries({ queryKey: ["recent"] }),
  ]);
}

export const drive = {
  createFolder: (folderId: string | null, name: string) =>
    unwrap(api.POST("/api/v1/folders", { body: { folderId, name } })),
  rename: (id: string, name: string) =>
    unwrap(api.POST("/api/v1/nodes/{id}/rename", { params: { path: { id } }, body: { name } })),
  move: (id: string, folderId: string | null) =>
    unwrap(api.POST("/api/v1/nodes/{id}/move", { params: { path: { id } }, body: { folderId } })),
  restoreVersion: (id: string, versionId: string) =>
    unwrap(api.POST("/api/v1/nodes/{id}/versions/{versionId}/restore", { params: { path: { id, versionId } } })),
  keepForever: (id: string, versionId: string, keepForever: boolean) =>
    unwrap(
      api.PUT("/api/v1/nodes/{id}/versions/{versionId}/keep-forever", {
        params: { path: { id, versionId } },
        body: { keepForever },
      }),
    ),
  /** Edits the current version's title, description and date taken (§13.5); null keeps what the file says. */
  setDetails: (id: string, body: { title: string | null; description: string | null; taken: string | null }) =>
    send(api.PUT("/api/v1/nodes/{id}/details", { params: { path: { id } }, body })),
  setTextPage: (id: string, pageNo: number, text: string) =>
    send(api.PUT("/api/v1/nodes/{id}/text/{pageNo}", { params: { path: { id, pageNo } }, body: { text } })),
  revertTextPage: (id: string, pageNo: number) =>
    send(api.DELETE("/api/v1/nodes/{id}/text/{pageNo}", { params: { path: { id, pageNo } } })),
  share: (id: string, userId: string, role: ShareRole): Promise<NodeAccess> =>
    unwrap(api.POST("/api/v1/nodes/{id}/shares", { params: { path: { id } }, body: { userId, role } })),
  changeShare: (shareId: string, role: ShareRole): Promise<NodeAccess> =>
    unwrap(api.PUT("/api/v1/shares/{id}", { params: { path: { id: shareId } }, body: { role } })),
  removeShare: (shareId: string) => send(api.DELETE("/api/v1/shares/{id}", { params: { path: { id: shareId } } })),
  /** Stops seeing something shared with the person directly. */
  leave: (id: string) => send(api.POST("/api/v1/nodes/{id}/leave", { params: { path: { id } } })),
  /** Opened from Shared with me: no longer New. */
  seen: (id: string) => send(api.POST("/api/v1/nodes/{id}/seen", { params: { path: { id } } })),
  /** Who would stop seeing these items if they moved to the folder (§9). */
  losingAccess: async (ids: string[], folderId: string | null): Promise<Person[]> =>
    (await unwrap(api.POST("/api/v1/nodes/move-check", { body: { ids, folderId } }))).losingAccess,
  trash: (ids: string[]) => send(api.POST("/api/v1/nodes/trash", { body: { ids } })),
  changeTags: (ids: string[], change: { add?: string[]; remove?: string[] }) =>
    send(api.POST("/api/v1/nodes/tags", { body: { ids, add: change.add ?? [], remove: change.remove ?? [] } })),
  star: (ids: string[], starred: boolean) =>
    send(api.POST(starred ? "/api/v1/nodes/star" : "/api/v1/nodes/unstar", { body: { ids } })),
  /** Tells the server the person opened a file, for their Recent page. Not audited (§18). */
  opened: (id: string) => send(api.POST("/api/v1/nodes/{id}/opened", { params: { path: { id } } })),
  /** Asks for a playable copy of a video the browser can't play, ahead of other work. 409 when none can be made. */
  requestPlayable: (id: string) => unwrap(api.POST("/api/v1/nodes/{id}/playable", { params: { path: { id } } })),
  /** Transcribes a recording again from the start, ahead of other work (§11.1). 409 when it isn't transcribed. */
  redoTranscript: (id: string): Promise<Transcript> =>
    unwrap(api.POST("/api/v1/nodes/{id}/transcript/redo", { params: { path: { id } } })),
  restore: async (ids: string[]): Promise<RestoredItem[]> =>
    (await unwrap(api.POST("/api/v1/trash/restore", { body: { ids } }))).items,
  deleteForever: (ids: string[]) => send(api.POST("/api/v1/trash/delete", { body: { ids } })),
  emptyTrash: async () => (await unwrap(api.POST("/api/v1/trash/empty"))).items,
  checkDuplicates: async (files: { name: string; size: number; sha256: string }[]): Promise<DuplicateCheckResult[]> =>
    (await unwrap(api.POST("/api/v1/uploads/check", { body: { files } }))).files,
  /** Makes a folder upload's folders in one go (§8.1); throws nameTaken or quotaExceeded before making any. */
  createFolderTree: async (
    folderId: string | null,
    paths: string[],
    onConflict: FolderConflictPolicy,
    totalBytes: number,
  ): Promise<FolderTreeEntry[]> =>
    (await unwrap(api.POST("/api/v1/folders/tree", { body: { folderId, paths, onConflict, totalBytes } }))).folders,
  copyFromContent: (
    folderId: string | null,
    name: string,
    sha256: string,
    clientModifiedAt: string | null,
    onConflict: NameConflictPolicy,
  ) => unwrap(api.POST("/api/v1/uploads/copy", { body: { folderId, name, sha256, clientModifiedAt, onConflict } })),
};

/** Names compare like the server does: NFC, case-insensitive (docs/design.md §6). */
export const nameKey = (name: string) => name.trim().normalize("NFC").toLowerCase();

/** "Kvitton / 2025" for a path, with the root's name first. */
export const pathLabel = (root: string, path: { name: string }[]) => [root, ...path.map((p) => p.name)].join(" / ");

/** Starts a browser download of a file's original bytes, without leaving the page. */
export function download(url: string) {
  const link = document.createElement("a");
  link.href = url;
  link.download = "";
  document.body.append(link);
  link.click();
  link.remove();
}

/**
 * What's said in a recording (§11.1). Under ["node", id] so the worker's notice when it's done refreshes it; while
 * it runs, lines arrive about a minute of sound at a time with no notice, so it's asked for again every 10 s.
 */
export const transcriptQuery = (id: string) =>
  queryOptions({
    queryKey: ["node", id, "transcript"],
    queryFn: (): Promise<Transcript> => unwrap(api.GET("/api/v1/nodes/{id}/transcript", { params: { path: { id } } })),
    refetchInterval: (query) => (query.state.data?.state === "running" ? 10_000 : false),
  });
