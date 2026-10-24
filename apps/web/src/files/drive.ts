import type {
  DuplicateCheckResult,
  FolderListing,
  NameConflictPolicy,
  NodeDetails,
  NodeDto,
  Problem,
  StorageUsage,
  VersionDto,
} from "@dokument/api";
import { thumbnailUrl } from "@dokument/api";
import { kindOf, type FileItem } from "@dokument/ui";
import { queryOptions, type QueryClient } from "@tanstack/react-query";
import type { useTranslation } from "react-i18next";
import { api } from "../api";

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
  modified: node.modifiedAt,
  owner: node.ownerName,
  thumbnail: node.thumbnail === "ready" ? thumbnailUrl(node.id, node.currentVersionId) : undefined,
  thumbnailPending: node.thumbnail === "pending",
  node,
});

/**
 * While the worker is still making thumbnails, the listing is polled so they appear on their own (§16.4): every 2 s
 * at first, then every 10 s for slow ones. SignalR replaces this with the live updates work.
 */
const pollWhilePending = (listing: FolderListing | undefined, updates: number) =>
  listing?.items.some((item) => item.thumbnail === "pending") ? (updates < 30 ? 2_000 : 10_000) : false;

export const folderQuery = (folderId: string | null) =>
  queryOptions({
    queryKey: ["folder", folderId ?? "root"],
    queryFn: (): Promise<FolderListing> =>
      unwrap(api.GET("/api/v1/nodes", { params: { query: folderId ? { folderId } : {} } })),
    refetchInterval: (query) => pollWhilePending(query.state.data, query.state.dataUpdateCount),
  });

export const nodeQuery = (id: string) =>
  queryOptions({
    queryKey: ["node", id],
    queryFn: (): Promise<NodeDetails> => unwrap(api.GET("/api/v1/nodes/{id}", { params: { path: { id } } })),
    refetchInterval: (query) =>
      query.state.data?.node.thumbnail === "pending" ? (query.state.dataUpdateCount < 30 ? 2_000 : 10_000) : false,
  });

export const versionsQuery = (id: string) =>
  queryOptions({
    queryKey: ["versions", id],
    queryFn: (): Promise<VersionDto[]> =>
      unwrap(api.GET("/api/v1/nodes/{id}/versions", { params: { path: { id } } })),
  });

export const storageQuery = queryOptions({
  queryKey: ["storage"],
  queryFn: (): Promise<StorageUsage> => unwrap(api.GET("/api/v1/storage")),
});

/** Refetches what a change to files in these folders can affect. */
export async function invalidateFolders(queryClient: QueryClient, folderIds: (string | null)[]) {
  await Promise.all([
    ...Array.from(new Set(folderIds.map((id) => id ?? "root")), (key) =>
      queryClient.invalidateQueries({ queryKey: ["folder", key] }),
    ),
    queryClient.invalidateQueries({ queryKey: ["storage"] }),
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
  checkDuplicates: async (files: { name: string; size: number; sha256: string }[]): Promise<DuplicateCheckResult[]> =>
    (await unwrap(api.POST("/api/v1/uploads/check", { body: { files } }))).files,
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
