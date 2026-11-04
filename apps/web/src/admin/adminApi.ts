import type {
  AdminSettings,
  AdminUser,
  CopyStats,
  MlEndpointStatus,
  StorageReport,
  TagRule,
  TagRulePreview,
  TextProgress,
  TranscriptionProgress,
} from "@dokument/api";
import { queryOptions } from "@tanstack/react-query";
import { api } from "../api";
import { send, unwrap } from "../files/drive";

export const adminSettingsQuery = queryOptions({
  queryKey: ["admin", "settings"],
  queryFn: (): Promise<AdminSettings> => unwrap(api.GET("/api/v1/admin/settings")),
});

export const adminUsersQuery = queryOptions({
  queryKey: ["admin", "users"],
  queryFn: (): Promise<AdminUser[]> => unwrap(api.GET("/api/v1/admin/users")),
});

export const storageReportQuery = queryOptions({
  queryKey: ["admin", "storage"],
  queryFn: (): Promise<StorageReport> => unwrap(api.GET("/api/v1/admin/storage")),
});

/** Checks every saved ML service on the server; slow ones take up to 5 s. */
export const mlStatusQuery = queryOptions({
  queryKey: ["admin", "ml", "status"],
  queryFn: (): Promise<MlEndpointStatus[]> => unwrap(api.GET("/api/v1/admin/ml/status")),
  refetchInterval: 60_000,
});

/** How far the worker is with reading text, refreshed while it has files waiting. */
export const textProgressQuery = queryOptions({
  queryKey: ["admin", "text-progress"],
  queryFn: (): Promise<TextProgress> => unwrap(api.GET("/api/v1/admin/text-recognition/progress")),
  refetchInterval: (query) => ((query.state.data?.waiting ?? 0) + (query.state.data?.running ?? 0) > 0 ? 5_000 : false),
});

/** Keyword rules (§13.4). Polled while one runs over every file, so its count fills in. */
export const tagRulesQuery = queryOptions({
  queryKey: ["admin", "tag-rules"],
  queryFn: async (): Promise<TagRule[]> => (await unwrap(api.GET("/api/v1/admin/tag-rules"))).rules,
  refetchInterval: (query) => (query.state.data?.some((rule) => rule.running) ? 2_000 : false),
});

/** Files per page in the rule preview. */
export const tagRulePreviewPage = 50;

/** Playable copies of videos, refreshed while some are still to make. */
export const videoCopiesQuery = queryOptions({
  queryKey: ["admin", "video-copies"],
  queryFn: (): Promise<CopyStats> => unwrap(api.GET("/api/v1/admin/videos/copies")),
  refetchInterval: (query) => ((query.state.data?.waiting ?? 0) > 0 ? 10_000 : false),
});

/** How far speech to text has come, refreshed while recordings are waiting or being listened to. */
export const transcriptionProgressQuery = queryOptions({
  queryKey: ["admin", "transcription-progress"],
  queryFn: (): Promise<TranscriptionProgress> => unwrap(api.GET("/api/v1/admin/transcription/progress")),
  refetchInterval: (query) => ((query.state.data?.waiting ?? 0) + (query.state.data?.running ?? 0) > 0 ? 10_000 : false),
});

export const admin = {
  createTagRule: (tag: string, words: string, enabled: boolean) =>
    unwrap(api.POST("/api/v1/admin/tag-rules", { body: { tag, words, enabled } })),
  updateTagRule: (id: string, tag: string, words: string, enabled: boolean) =>
    unwrap(api.PUT("/api/v1/admin/tag-rules/{id}", { params: { path: { id } }, body: { tag, words, enabled } })),
  deleteTagRule: (id: string) => send(api.DELETE("/api/v1/admin/tag-rules/{id}", { params: { path: { id } } })),
  previewTagRule: (tag: string, words: string, offset: number): Promise<TagRulePreview> =>
    unwrap(
      api.POST("/api/v1/admin/tag-rules/preview", {
        body: { tag: tag || null, words, offset, limit: tagRulePreviewPage },
      }),
    ),
  putStorage: (defaultQuotaBytes: number | null) =>
    unwrap(api.PUT("/api/v1/admin/settings/storage", { body: { defaultQuotaBytes } })),
  putVersions: (keepDays: number, maxVersions: number) =>
    unwrap(api.PUT("/api/v1/admin/settings/versions", { body: { keepDays, maxVersions } })),
  putTrash: (keepDays: number) => unwrap(api.PUT("/api/v1/admin/settings/trash", { body: { keepDays } })),
  putMl: (endpoints: { url: string; apiKey: string | null; keepApiKey: boolean }[]) =>
    unwrap(api.PUT("/api/v1/admin/settings/ml", { body: { endpoints } })),
  putTextRecognition: (enabled: boolean) =>
    unwrap(api.PUT("/api/v1/admin/settings/text-recognition", { body: { enabled } })),
  putTranscription: (enabled: boolean, model: string, videos: boolean) =>
    unwrap(api.PUT("/api/v1/admin/settings/transcription", { body: { enabled, model, videos } })),
  rereadText: () => unwrap(api.POST("/api/v1/admin/text-recognition/reread")),
  redoTranscripts: () => unwrap(api.POST("/api/v1/admin/transcription/redo")),
  putVideos: (videos: AdminSettings["videos"]) => unwrap(api.PUT("/api/v1/admin/settings/videos", { body: videos })),
  putOffice: (gotenbergUrl: string | null) => unwrap(api.PUT("/api/v1/admin/settings/office", { body: { gotenbergUrl } })),
  putWordViewer: (enabled: boolean) => unwrap(api.PUT("/api/v1/admin/settings/word-viewer", { body: { enabled } })),
  testOffice: (url: string) => unwrap(api.POST("/api/v1/admin/office/test", { body: { url } })),
  deleteVideoCopies: () => unwrap(api.DELETE("/api/v1/admin/videos/copies")),
  putScanner: (address: string | null) => unwrap(api.PUT("/api/v1/admin/settings/scanner", { body: { address } })),
  testMl: (url: string, apiKey: string | null) => unwrap(api.POST("/api/v1/admin/ml/test", { body: { url, apiKey } })),
  testScanner: (address: string) => unwrap(api.POST("/api/v1/admin/scanner/test", { body: { address } })),
  putQuota: (id: string, kind: AdminUser["quota"]["kind"], bytes: number | null) =>
    unwrap(api.PUT("/api/v1/admin/users/{id}/quota", { params: { path: { id } }, body: { kind, bytes } })),
};

export const GiB = 1024 ** 3;

/** Bytes as the GiB number shown in a field: up to two decimals, no trailing zeros. */
export const toGib = (bytes: number) => String(Math.round((bytes / GiB) * 100) / 100);

/** The GiB a person typed, as bytes; null when it isn't a size above 0. */
export function parseGib(value: string): number | null {
  const number = Number(value.trim().replace(",", "."));
  return Number.isFinite(number) && number > 0 ? Math.round(number * GiB) : null;
}
