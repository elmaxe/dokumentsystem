import type { FiledFile, FilingPreview, FilingRule } from "@dokument/api";
import { keepPreviousData, queryOptions, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { useEffect } from "react";
import { useTranslation } from "react-i18next";
import { toast } from "../ui/Snackbar";
import { api, ApiError, send, unwrap } from "./api";
import { onFilings } from "./live";

/** The person's filing rules, in order (docs/design.md §13.6); the web's query key, so live notices reach it. */
export const filingRulesQuery = queryOptions({
  queryKey: ["filing-rules"],
  queryFn: async (): Promise<FilingRule[]> => (await unwrap(api().GET("/api/v1/me/filing-rules"))).rules,
});

/** Where a couple of the files that have the tag now would go, without moving anything. */
export const filingPreviewQuery = (tag: string, folder: string) =>
  queryOptions({
    queryKey: ["filing-rule-preview", tag, folder],
    queryFn: (): Promise<FilingPreview> =>
      unwrap(api().POST("/api/v1/me/filing-rules/preview", { body: { tag, folder } })),
    enabled: tag.length > 0,
    placeholderData: keepPreviousData,
    retry: false,
  });

export type FilingRuleInput = { tag: string; folder: string; enabled: boolean; fileExisting?: boolean };

const body = (rule: FilingRuleInput) => ({ ...rule, fileExisting: rule.fileExisting ?? false });

export const filing = {
  create: (rule: FilingRuleInput) => unwrap(api().POST("/api/v1/me/filing-rules", { body: body(rule) })),
  update: (id: string, rule: FilingRuleInput) =>
    unwrap(api().PUT("/api/v1/me/filing-rules/{id}", { params: { path: { id } }, body: body(rule) })),
  remove: (id: string) => send(api().DELETE("/api/v1/me/filing-rules/{id}", { params: { path: { id } } })),
  reorder: (ids: string[]) => send(api().PUT("/api/v1/me/filing-rules/order", { body: { ids } })),
  recent: async (after?: string): Promise<FiledFile[]> =>
    (await unwrap(api().GET("/api/v1/filings/recent", { params: { query: after ? { after } : {} } }))).items,
  undo: (nodeId: string) => send(api().POST("/api/v1/filings/{id}/undo", { params: { path: { id: nodeId } } })),
};

/** Whether a save was refused because the person already has a rule for the tag. */
export const isTaken = (error: unknown) => error instanceof ApiError && error.problem.code === "nameTaken";

/** Refetches what a filing changes: the rules' counts and the folders files moved between. */
export const invalidateFilings = (queryClient: QueryClient) =>
  Promise.all(
    [["folder"], ["node"], ["recent"], ["search"], filingRulesQuery.queryKey].map((queryKey) =>
      queryClient.invalidateQueries({ queryKey }),
    ),
  );

/**
 * Says when a filing rule moved the person's files, wherever they are in the app, with Undo, as the web does
 * (§13.6). The worker's notice only says that something was filed; the files come from the API, newer than the last
 * ones shown.
 */
export function useFiledMessages() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();

  useEffect(() => {
    let after: string | undefined;
    let timer: ReturnType<typeof setTimeout> | undefined;
    // Files filed before the app opened were announced elsewhere, or not at all, so start after them.
    const ready = filing
      .recent()
      .then((filed) => (after = filed.at(-1)?.filedAt ?? after))
      .catch(() => undefined);
    const show = async () => {
      try {
        await ready;
        const filed = await filing.recent(after);
        const [first] = filed;
        const last = filed.at(-1);
        if (!first || !last) return;
        after = last.filedAt;
        const undo = async () => {
          try {
            await Promise.all(filed.map((file) => filing.undo(file.nodeId)));
            toast(t("filing.undone", { count: filed.length }));
          } catch {
            toast(t("errors.generic"));
          }
          // The app skips live notices about its own changes, so it refetches what the undo moved.
          await invalidateFilings(queryClient);
        };
        const folders = new Set(filed.map((file) => file.folder));
        toast(
          filed.length === 1
            ? t("filing.filed", { name: first.name, folder: first.folder })
            : folders.size === 1
              ? t("filing.filedMany", { count: filed.length, folder: first.folder })
              : t("filing.filedSeveral", { count: filed.length }),
          { label: t("filing.undo"), onPress: () => void undo() },
        );
      } catch {
        // The files are filed either way; the message is only a courtesy.
      }
    };
    // Filing many files at once sends a notice per file, so wait for them to settle and say it once.
    const stop = onFilings(() => {
      clearTimeout(timer);
      timer = setTimeout(() => void show(), 800);
    });
    return () => {
      clearTimeout(timer);
      stop();
    };
  }, [t, queryClient]);
}
