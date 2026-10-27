import createClient, { type Middleware } from "openapi-fetch";
import { liveClientHeader } from "./live";
import type { components, paths } from "./schema";

export * from "./live";

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
export type FolderConflictPolicy = components["schemas"]["FolderConflictPolicy"];
export type FolderTreeEntry = components["schemas"]["FolderTreeEntry"];
export type StorageUsage = components["schemas"]["StorageUsage"];
export type TrashListing = components["schemas"]["TrashListing"];
export type TrashItem = components["schemas"]["TrashItem"];
export type RestoredItem = components["schemas"]["RestoredItem"];
export type StarredListing = components["schemas"]["StarredListing"];
export type StarredItem = components["schemas"]["StarredItem"];
export type RecentListing = components["schemas"]["RecentListing"];
export type RecentItem = components["schemas"]["RecentItem"];
export type RecentAction = components["schemas"]["RecentAction"];
export type ThumbnailState = components["schemas"]["ThumbnailState"];
export type AdminSettings = components["schemas"]["AdminSettingsDto"];
export type AdminUser = components["schemas"]["AdminUser"];
export type StorageReport = components["schemas"]["StorageReport"];
export type MlEndpointStatus = components["schemas"]["MlEndpointStatus"];
export type MlEndpointUpdate = components["schemas"]["MlEndpointUpdate"];
export type ProbeResult = components["schemas"]["ProbeResult"];
export type Quota = components["schemas"]["QuotaDto"];
export type SearchResponse = components["schemas"]["SearchResponse"];
export type SearchResult = components["schemas"]["SearchResultDto"];
export type SearchSort = components["schemas"]["SearchSort"];
export type MatchSource = components["schemas"]["MatchSource"];
export type Snippet = components["schemas"]["SnippetDto"];
export type JobKind = components["schemas"]["JobKind"];
export type JobStatus = components["schemas"]["JobStatus"];
export type JobQueue = components["schemas"]["JobQueueDto"];
export type JobList = components["schemas"]["JobList"];
export type Job = components["schemas"]["JobDto"];
export type JobFile = components["schemas"]["JobFileDto"];
export type ActiveStep = components["schemas"]["ActiveStepDto"];
export type FileProcessing = components["schemas"]["FileProcessing"];
export type ProcessingStep = components["schemas"]["ProcessingStepDto"];
export type FileDetailsDto = components["schemas"]["FileDetailsDto"];
export type FileTextDto = components["schemas"]["FileTextDto"];
export type TextPage = components["schemas"]["TextPageDto"];
export type TakenDto = components["schemas"]["TakenDto"];
export type DetailField = components["schemas"]["DetailField"];
export type StepState = components["schemas"]["StepState"];
export type SuggestResponse = components["schemas"]["SuggestResponse"];
export type Suggestion = components["schemas"]["SuggestionDto"];
export type MatchesResponse = components["schemas"]["MatchesResponse"];
export type PageMatch = components["schemas"]["PageMatchDto"];
export type TextProgress = components["schemas"]["TextProgress"];

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
  options: {
    baseUrl?: string;
    token?: string;
    csrfToken?: () => string | null | undefined;
    /** Names this client on mutations, so it can skip live notices about its own changes (§15.4). */
    clientId?: string;
  } = {},
) {
  const client = createClient<paths>({
    baseUrl: options.baseUrl ?? "",
    credentials: "include",
    headers: options.token ? { Authorization: `Bearer ${options.token}` } : undefined,
  });

  const { csrfToken, clientId } = options;
  if (csrfToken || clientId) {
    const mutations: Middleware = {
      onRequest({ request }) {
        if (["GET", "HEAD", "OPTIONS"].includes(request.method)) return request;
        const token = csrfToken?.();
        if (token) request.headers.set(csrfHeader, token);
        if (clientId) request.headers.set(liveClientHeader, clientId);
        return request;
      },
    };
    client.use(mutations);
  }

  return client;
}

export type ApiClient = ReturnType<typeof createApiClient>;
