import type { FileOrigin, PageSheets, PdfOperation } from "@dokument/api";
import type { TrayTask } from "@dokument/ui";
import { queryOptions } from "@tanstack/react-query";
import { createContext, use } from "react";
import { api } from "../api";
import { unwrap } from "./drive";

/** Merging and splitting PDFs (docs/design.md §8.4). */
export const pdf = {
  merge: (body: { ids: string[]; folderId: string | null; name: string; trashOriginals: boolean }) =>
    unwrap(api.POST("/api/v1/pdf/merge", { body })),
  split: (body: {
    id: string;
    parts: { name: string; pages: string }[];
    newFolderName: string | null;
    trashOriginal: boolean;
  }) => unwrap(api.POST("/api/v1/pdf/split", { body })),
  cancel: (id: string) => unwrap(api.DELETE("/api/v1/pdf/operations/{id}", { params: { path: { id } } })),
};

export const active = (op: PdfOperation) => op.state === "waiting" || op.state === "working" || op.state === "saving";

/** The person's merges and splits from the last week, asked for every second while one is going. */
export const pdfOperationsQuery = queryOptions({
  queryKey: ["pdfOperations"],
  queryFn: async (): Promise<PdfOperation[]> => (await unwrap(api.GET("/api/v1/pdf/operations"))).items,
  refetchInterval: (query) => (query.state.data?.some(active) ? 1_000 : false),
});

/**
 * A PDF's pages as small images for the split screen. Asking starts them; until they're made it's asked for again
 * every 2 s. 409 when the file can't be read.
 */
export const pageSheetsQuery = (id: string, versionId: string | null) =>
  queryOptions({
    queryKey: ["pageSheets", id, versionId],
    queryFn: (): Promise<PageSheets> => unwrap(api.POST("/api/v1/nodes/{id}/page-sheets", { params: { path: { id } } })),
    refetchInterval: (query) => (query.state.data && !query.state.data.ready ? 2_000 : false),
    retry: false,
  });

/** What a merged or split file was made from; null for other files. */
export const originQuery = (id: string, versionId: string | null) =>
  queryOptions({
    queryKey: ["origin", id, versionId],
    queryFn: async (): Promise<FileOrigin | null> => {
      const { data, response } = await api.GET("/api/v1/nodes/{id}/origin", { params: { path: { id } } });
      return response.status === 204 ? null : (data ?? null);
    },
  });

export type PdfTasks = {
  tasks: TrayTask[];
  operations: PdfOperation[];
  /** Shows an operation that was just started in the tray. */
  started: (op: PdfOperation) => void;
  cancel: (id: string) => Promise<void>;
  dismiss: (id: string) => void;
  /** Takes the finished ones out of the tray. */
  clear: () => void;
  /** Trashes the new files that have the same bytes as a file that was already there, and takes the row away. */
  removeCopies: (id: string) => Promise<number>;
};

export const PdfTasksContext = createContext<PdfTasks | null>(null);

export function usePdfTasks(): PdfTasks {
  const tasks = use(PdfTasksContext);
  if (!tasks) throw new Error("usePdfTasks needs a PdfTasksProvider");
  return tasks;
}

