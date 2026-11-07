import { pageSheetUrl, type NodeDto, type PageSheets, type PdfOperation } from "@dokument/api";
import { queryOptions, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef, useSyncExternalStore } from "react";
import { api, ApiError, authHeaders, baseUrl, unwrap } from "./api";

// Merging and splitting PDFs (docs/design.md §8.4). The query keys match the web app's.

export const pdf = {
  merge: (body: { ids: string[]; folderId: string | null; name: string; trashOriginals: boolean }) =>
    unwrap(api().POST("/api/v1/pdf/merge", { body })),
  split: (body: {
    id: string;
    parts: { name: string; pages: string }[];
    newFolderName: string | null;
    trashOriginal: boolean;
  }) => unwrap(api().POST("/api/v1/pdf/split", { body })),
  cancel: (id: string) => unwrap(api().DELETE("/api/v1/pdf/operations/{id}", { params: { path: { id } } })),
};

/**
 * A PDF the server could read, where the person can edit: only those can be merged or split, since the new files go
 * next to it (§9).
 */
export const readablePdf = (node: NodeDto) =>
  !!node.pdf?.readable && (node.access === "owner" || node.access === "editor");

export const isActive = (op: PdfOperation) => op.state === "waiting" || op.state === "working" || op.state === "saving";

/** The person's merges and splits from the last week, asked for every second while one is going. */
export const pdfOperationsQuery = queryOptions({
  queryKey: ["pdfOperations"],
  queryFn: async (): Promise<PdfOperation[]> => (await unwrap(api().GET("/api/v1/pdf/operations"))).items,
  refetchInterval: (query) => (query.state.data?.some(isActive) ? 1_000 : false),
});

/** Where each page's image is; asking starts them, and it's asked again every 2 s until they're made. */
export const pageSheetsQuery = (id: string, versionId: string | null) =>
  queryOptions({
    queryKey: ["pageSheets", id, versionId],
    queryFn: (): Promise<PageSheets> =>
      unwrap(api().POST("/api/v1/nodes/{id}/page-sheets", { params: { path: { id } } })),
    refetchInterval: (query) => (query.state.data && !query.state.data.ready ? 2_000 : false),
    retry: false,
  });

/** One sheet of page images as expo-image loads it, with the session. */
export const pageSheetSource = (id: string, sheet: number, versionId: string | null) => ({
  uri: pageSheetUrl(id, sheet, baseUrl()),
  headers: authHeaders(),
  cacheKey: `${id}/${versionId ?? ""}/pages-${sheet}`,
});

// The operations started on this phone, shown above the Scan button until dismissed.
let shown: string[] = [];
const listeners = new Set<() => void>();
const changed = (next: string[]) => {
  shown = next;
  listeners.forEach((listener) => listener());
};
const subscribe = (listener: () => void) => {
  listeners.add(listener);
  return () => listeners.delete(listener);
};

export function useStartedPdf() {
  const queryClient = useQueryClient();
  return (op: PdfOperation) => {
    queryClient.setQueryData(pdfOperationsQuery.queryKey, (old) => [op, ...(old ?? []).filter((o) => o.id !== op.id)]);
    changed([...shown, op.id]);
  };
}

export const dismissPdf = (id: string) => changed(shown.filter((other) => other !== id));

/** The operations started on this phone and not dismissed. When one finishes, the folders and trash are fetched again. */
export function usePdfTasks(): PdfOperation[] {
  const ids = useSyncExternalStore(subscribe, () => shown);
  const queryClient = useQueryClient();
  const list = useQuery({ ...pdfOperationsQuery, enabled: ids.length > 0 });
  const states = useRef(new Map<string, PdfOperation["state"]>());
  useEffect(() => {
    let finished = false;
    for (const op of list.data ?? []) {
      const before = states.current.get(op.id);
      states.current.set(op.id, op.state);
      if (before && before !== op.state && op.state === "done") finished = true;
    }
    if (finished) {
      for (const key of ["folder", "trash", "storage", "recent", "starred"]) {
        void queryClient.invalidateQueries({ queryKey: [key] });
      }
    }
  }, [list.data, queryClient]);
  return (list.data ?? []).filter((op) => ids.includes(op.id));
}

/** Why a merge or split was refused: the server's sentence, which names the file, or the general one. */
export function pdfProblem(t: (key: string) => string, error: unknown): string {
  if (error instanceof ApiError) {
    switch (error.problem.code) {
      case "invalidRequest":
        if (error.problem.detail) return error.problem.detail;
        break;
      case "quotaExceeded":
        return t("errors.quotaExceeded");
      case "notFound":
        return t("errors.notFound");
      case "forbidden":
        return t("errors.forbidden");
    }
  }
  return t("errors.generic");
}
