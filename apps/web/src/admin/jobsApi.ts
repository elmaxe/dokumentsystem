import type { JobKind, JobList, JobQueue, JobStatus } from "@dokument/api";
import { queryOptions } from "@tanstack/react-query";
import { api } from "../api";
import { send, unwrap } from "../files/drive";

export type JobFilter = { status: Exclude<JobStatus, "done">; type?: JobKind; limit: number };

/** The Jobs page refreshes every 2 s while it's open (§10.1). */
const refresh = 2_000;

export const jobQueuesQuery = queryOptions({
  queryKey: ["admin", "jobs", "queues"],
  queryFn: (): Promise<JobQueue[]> => unwrap(api.GET("/api/v1/admin/jobs/queues")),
  refetchInterval: refresh,
});

export const jobsQuery = ({ status, type, limit }: JobFilter) =>
  queryOptions({
    queryKey: ["admin", "jobs", "list", status, type ?? "all", limit],
    queryFn: (): Promise<JobList> =>
      unwrap(api.GET("/api/v1/admin/jobs", { params: { query: { status, type, limit } } })),
    refetchInterval: refresh,
    placeholderData: (previous) => previous,
  });

export const jobs = {
  pause: (type: JobKind, paused: boolean) =>
    unwrap(api.PUT("/api/v1/admin/jobs/queues/{type}/paused", { params: { path: { type } }, body: { paused } })),
  retry: (id: string) => send(api.POST("/api/v1/admin/jobs/{id}/retry", { params: { path: { id } } })),
  retryFailed: (type?: JobKind) => unwrap(api.POST("/api/v1/admin/jobs/retry-failed", { params: { query: { type } } })),
  clear: (id: string) => send(api.DELETE("/api/v1/admin/jobs/{id}", { params: { path: { id } } })),
};
