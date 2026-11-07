import type { FilingRun, FilingRunPreview, FilingRunRequest } from "@dokument/api";
import { useSnackbar } from "@dokument/ui";
import { queryOptions, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef } from "react";
import { useTranslation } from "react-i18next";
import { api } from "../api";
import { ApiError, errorMessage, send, unwrap } from "../files/drive";
import { filingRulesQuery } from "./filing";

const active = (run: FilingRun | null | undefined) => run?.status === "queued" || run?.status === "running";

/**
 * The person's latest run of their filing rules (§13.6), or null. While one is going it is asked for every second,
 * for its progress; the worker's live notice says when it has finished.
 */
export const latestRunQuery = queryOptions({
  queryKey: ["filing-run"],
  queryFn: async (): Promise<FilingRun | null> => {
    const { data, error, response } = await api.GET("/api/v1/me/filing-runs/latest");
    if (response.status === 204) return null;
    if (!response.ok || !data) throw new ApiError(response.status, (error ?? {}) as never);
    return data;
  },
  refetchInterval: (query) => (active(query.state.data) ? 1000 : false),
});

export const filingRuns = {
  preview: (body: FilingRunRequest): Promise<FilingRunPreview> =>
    unwrap(api.POST("/api/v1/me/filing-runs/preview", { body })),
  start: (body: FilingRunRequest): Promise<FilingRun> => unwrap(api.POST("/api/v1/me/filing-runs", { body })),
  stop: (id: string) => send(api.POST("/api/v1/me/filing-runs/{id}/stop", { params: { path: { id } } })),
  undo: async (id: string): Promise<number> =>
    (await unwrap(api.POST("/api/v1/me/filing-runs/{id}/undo", { params: { path: { id } } }))).files,
};

/** What a run is asked to do: one rule or all, over some files and folders or all of them. */
export type RunScope = { ruleId?: string; ruleTag?: string; nodeIds?: string[]; rules?: number };

/** Refetches what a run's moves or Undo change. */
function useRefreshAfterRun() {
  const queryClient = useQueryClient();
  return () =>
    Promise.all(
      [["folder"], ["node"], ["recent"], ["starred"], filingRulesQuery.queryKey, latestRunQuery.queryKey].map(
        (queryKey) => queryClient.invalidateQueries({ queryKey }),
      ),
    );
}

/** Undoes a whole run and says how many files went back. */
export function useUndoRun() {
  const { t } = useTranslation();
  const toast = useSnackbar();
  const refresh = useRefreshAfterRun();
  return async (run: FilingRun) => {
    try {
      const back = await filingRuns.undo(run.id);
      toast(t("filing.run.undone", { count: back }));
    } catch (error) {
      toast(errorMessage(t, error));
    }
    await refresh();
  };
}

/** "Filed 27 files in 3 folders", for a run that has finished. */
export function runResult(t: ReturnType<typeof useTranslation>["t"], run: FilingRun) {
  if (run.status === "stopped") return t("filing.run.stopped", { count: run.moved });
  if (run.moved === 0) return t("filing.run.filedNone");
  return run.folder
    ? t("filing.run.filedOne", { count: run.moved, folder: run.folder })
    : t("filing.run.filedMany", { count: run.moved, folders: run.folders });
}

/**
 * Says when a run the person started finishes, wherever they are in the app, once, with Undo for the whole run. Runs
 * that finished before the app opened aren't announced; Settings shows the last one.
 */
export function useFilingRunMessages() {
  const { t } = useTranslation();
  const toast = useSnackbar();
  const latest = useQuery(latestRunQuery);
  const undo = useUndoRun();
  const refresh = useRefreshAfterRun();
  const watching = useRef<string | null>(null);
  const run = latest.data;

  useEffect(() => {
    if (!run) return;
    if (active(run)) {
      watching.current = run.id;
      return;
    }
    if (watching.current !== run.id) return;
    watching.current = null;
    void refresh();
    toast(runResult(t, run), run.canUndo ? { label: t("filing.run.undo"), onClick: () => void undo(run) } : undefined);
  }, [run, t, toast, undo, refresh]);
}

