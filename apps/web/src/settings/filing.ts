import type { FiledFile, FilingPreview, FilingRule } from "@dokument/api";
import { useSnackbar } from "@dokument/ui";
import { queryOptions, useQueryClient } from "@tanstack/react-query";
import { useEffect } from "react";
import { useTranslation } from "react-i18next";
import { api } from "../api";
import { errorMessage, send, unwrap } from "../files/drive";
import { onFilings } from "../live";

/** The person's filing rules, in order (§13.6). */
export const filingRulesQuery = queryOptions({
  queryKey: ["filing-rules"],
  queryFn: async (): Promise<FilingRule[]> => (await unwrap(api.GET("/api/v1/me/filing-rules"))).rules,
});

export type FilingRuleInput = { tag: string; folder: string; enabled: boolean; fileExisting?: boolean };

const body = (rule: FilingRuleInput) => ({ ...rule, fileExisting: rule.fileExisting ?? false });

export const filing = {
  create: (rule: FilingRuleInput) => unwrap(api.POST("/api/v1/me/filing-rules", { body: body(rule) })),
  update: (id: string, rule: FilingRuleInput) =>
    unwrap(api.PUT("/api/v1/me/filing-rules/{id}", { params: { path: { id } }, body: body(rule) })),
  remove: (id: string) => send(api.DELETE("/api/v1/me/filing-rules/{id}", { params: { path: { id } } })),
  reorder: (ids: string[]) => send(api.PUT("/api/v1/me/filing-rules/order", { body: { ids } })),
  preview: (tag: string, folder: string): Promise<FilingPreview> =>
    unwrap(api.POST("/api/v1/me/filing-rules/preview", { body: { tag, folder } })),
  recent: async (after?: string): Promise<FiledFile[]> =>
    (await unwrap(api.GET("/api/v1/filings/recent", { params: { query: after ? { after } : {} } }))).items,
  undo: (nodeId: string) => send(api.POST("/api/v1/filings/{id}/undo", { params: { path: { id: nodeId } } })),
};

/** The placeholders a folder may hold, as the server reads them. */
const placeholder = /\{[^{}]*\}/g;

/** Why a folder template won't do, checked as the person types; the server checks names too. */
export function folderProblem(template: string): "empty" | "placeholder" | null {
  if (template.split("/").every((folder) => folder.trim() === "")) return "empty";
  const unknown = [...template.matchAll(placeholder)].some((m) => !/^\{(year|month)\}$/i.test(m[0]));
  return unknown || /[{}]/.test(template.replace(placeholder, "")) ? "placeholder" : null;
}

/** "Kvitton/{year}" as "Kvitton / 2026", for this month. */
export function exampleFolder(template: string, now = new Date()): string {
  return template
    .split("/")
    .map((folder) => folder.trim())
    .filter(Boolean)
    .map((folder) =>
      folder.replace(placeholder, (m) =>
        m.toLowerCase() === "{year}" ? String(now.getFullYear()) : String(now.getMonth() + 1).padStart(2, "0"),
      ),
    )
    .join(" / ");
}

/**
 * Says when a filing rule moved the person's files, wherever they are in the app, with Undo (§13.6). The worker's
 * notice only says that something was filed; the files come from the API, newer than the last ones shown.
 */
export function useFiledMessages() {
  const { t } = useTranslation();
  const toast = useSnackbar();
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
          } catch (error) {
            toast(errorMessage(t, error));
          }
          await Promise.all(
            [["folder"], ["node"], ["recent"], filingRulesQuery.queryKey].map((queryKey) =>
              queryClient.invalidateQueries({ queryKey }),
            ),
          );
        };
        const folders = new Set(filed.map((file) => file.folder));
        const text =
          filed.length === 1
            ? t("filing.filed", { name: first.name, folder: first.folder })
            : folders.size === 1
              ? t("filing.filedMany", { count: filed.length, folder: first.folder })
              : t("filing.filedSeveral", { count: filed.length });
        toast(text, { label: t("filing.undo"), onClick: () => void undo() });
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
  }, [t, toast, queryClient]);
}
