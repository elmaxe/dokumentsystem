import createClient, { type Middleware } from "openapi-fetch";
import type { components, paths } from "./schema";

export type { components, paths };
export type ServerInfo = components["schemas"]["ServerInfo"];
export type AuthConfig = components["schemas"]["AuthConfig"];
export type CurrentSession = components["schemas"]["CurrentSession"];
export type UserProfile = components["schemas"]["UserProfile"];
export type SessionInfo = components["schemas"]["SessionInfo"];
export type NodeDto = components["schemas"]["NodeDto"];
export type NodeKind = components["schemas"]["NodeKind"];
export type PathEntry = components["schemas"]["PathEntry"];
export type FolderListing = components["schemas"]["FolderListing"];
export type NodeDetails = components["schemas"]["NodeDetails"];
export type VersionDto = components["schemas"]["VersionDto"];
export type DuplicateMatch = components["schemas"]["DuplicateMatch"];
export type DuplicateCheckResult = components["schemas"]["DuplicateCheckResult"];
export type NameConflictPolicy = components["schemas"]["NameConflictPolicy"];
export type StorageUsage = components["schemas"]["StorageUsage"];
export type TrashListing = components["schemas"]["TrashListing"];
export type TrashItem = components["schemas"]["TrashItem"];
export type RestoredItem = components["schemas"]["RestoredItem"];
export type ThumbnailState = components["schemas"]["ThumbnailState"];
export type AdminSettings = components["schemas"]["AdminSettingsDto"];
export type AdminUser = components["schemas"]["AdminUser"];
export type StorageReport = components["schemas"]["StorageReport"];
export type MlEndpointStatus = components["schemas"]["MlEndpointStatus"];
export type MlEndpointUpdate = components["schemas"]["MlEndpointUpdate"];
export type ProbeResult = components["schemas"]["ProbeResult"];
export type Quota = components["schemas"]["QuotaDto"];

/** The tus endpoint for resumable uploads (docs/design.md §8.1). */
export const uploadsPath = "/api/v1/uploads";

/** Set on the tus response that completes an upload: the id of the file it produced. */
export const uploadNodeIdHeader = "Dokument-Node-Id";

/** Where a file's original bytes are downloaded from: the current version unless one is given. Downloads are audited. */
export function contentUrl(nodeId: string, versionId?: string, baseUrl = ""): string {
  return `${baseUrl}/api/v1/nodes/${nodeId}/content${versionId ? `?versionId=${versionId}` : ""}`;
}

/**
 * A file's thumbnail: small (256 px) for lists or medium (640 px) for the grid. Pass the current version id so the
 * browser's cache follows new versions. Only ready once the node's `thumbnail` is "ready".
 */
export function thumbnailUrl(nodeId: string, versionId?: string | null, size: "small" | "medium" = "medium", baseUrl = ""): string {
  const query = new URLSearchParams({ ...(versionId ? { versionId } : {}), ...(size === "small" ? { size } : {}) }).toString();
  return `${baseUrl}/api/v1/nodes/${nodeId}/thumbnail${query ? `?${query}` : ""}`;
}

/** What the viewer loads: a rendition for images browsers can't show, otherwise the original, inline. Not audited. */
export function previewUrl(nodeId: string, versionId?: string | null, baseUrl = ""): string {
  return `${baseUrl}/api/v1/nodes/${nodeId}/preview${versionId ? `?versionId=${versionId}` : ""}`;
}

/** The RFC 9457 problem body the server sends for refused requests. File operations add a machine-readable code. */
export type Problem = {
  title?: string;
  detail?: string;
  status?: number;
  code?: "notFound" | "invalidRequest" | "nameTaken" | "quotaExceeded" | "uploadMismatch" | "uploadBusy";
  conflictingNodeId?: string;
};

/** The header that carries the session's CSRF token on mutations made with the session cookie (docs/design.md §5). */
export const csrfHeader = "X-CSRF-Token";

/** Where the browser goes to sign in. It's a page navigation, not a fetch, because it redirects to the IdP. */
export function loginUrl(returnTo: string, baseUrl = ""): string {
  return `${baseUrl}/api/v1/auth/login?returnTo=${encodeURIComponent(returnTo)}`;
}

/**
 * A client for the Dokumentsystem REST API. Web uses same-origin cookies and passes `csrfToken`, which is read
 * before each mutation; mobile passes a base URL and bearer token.
 */
export function createApiClient(
  options: { baseUrl?: string; token?: string; csrfToken?: () => string | null | undefined } = {},
) {
  const client = createClient<paths>({
    baseUrl: options.baseUrl ?? "",
    credentials: "include",
    headers: options.token ? { Authorization: `Bearer ${options.token}` } : undefined,
  });

  const { csrfToken } = options;
  if (csrfToken) {
    const csrf: Middleware = {
      onRequest({ request }) {
        const token = csrfToken();
        if (token && !["GET", "HEAD", "OPTIONS"].includes(request.method)) {
          request.headers.set(csrfHeader, token);
        }
        return request;
      },
    };
    client.use(csrf);
  }

  return client;
}

export type ApiClient = ReturnType<typeof createApiClient>;
