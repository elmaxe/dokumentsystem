import type { AdminSettings, AdminUser, MlEndpointStatus, StorageReport } from "@dokument/api";
import { queryOptions } from "@tanstack/react-query";
import { api } from "../api";
import { unwrap } from "../files/drive";

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

export const admin = {
  putStorage: (defaultQuotaBytes: number | null) =>
    unwrap(api.PUT("/api/v1/admin/settings/storage", { body: { defaultQuotaBytes } })),
  putVersions: (keepDays: number, maxVersions: number) =>
    unwrap(api.PUT("/api/v1/admin/settings/versions", { body: { keepDays, maxVersions } })),
  putTrash: (keepDays: number) => unwrap(api.PUT("/api/v1/admin/settings/trash", { body: { keepDays } })),
  putMl: (endpoints: { url: string; apiKey: string | null; keepApiKey: boolean }[]) =>
    unwrap(api.PUT("/api/v1/admin/settings/ml", { body: { endpoints } })),
  putScanner: (address: string | null) => unwrap(api.PUT("/api/v1/admin/settings/scanner", { body: { address } })),
  testMl: (url: string, apiKey: string | null) => unwrap(api.POST("/api/v1/admin/ml/test", { body: { url, apiKey } })),
  testScanner: (address: string) => unwrap(api.POST("/api/v1/admin/scanner/test", { body: { address } })),
  putQuota: (id: string, kind: AdminUser["quota"]["kind"], bytes: number | null) =>
    unwrap(api.PUT("/api/v1/admin/users/{id}/quota", { params: { path: { id } }, body: { kind, bytes } })),
};

export const GB = 1_000_000_000;

/** Bytes as the GB number shown in a field: up to two decimals, no trailing zeros. */
export const toGb = (bytes: number) => String(Math.round((bytes / GB) * 100) / 100);

/** The GB a person typed, as bytes; null when it isn't a size above 0. */
export function parseGb(value: string): number | null {
  const number = Number(value.trim().replace(",", "."));
  return Number.isFinite(number) && number > 0 ? Math.round(number * GB) : null;
}
