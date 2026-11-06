import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef, useState, type ReactNode } from "react";
import type { PdfOperation } from "@dokument/api";
import { drive, invalidateTrash } from "./drive";
import { active, pdf, pdfOperationsQuery, PdfTasksContext, type PdfTasks } from "./pdf";

/**
 * Keeps the tray's merge and split rows: the ones started in this tab, and any still going from before a reload.
 * When one finishes, the folders it touched are fetched again.
 */
export function PdfTasksProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient();
  const [shown, setShown] = useState<string[]>([]);
  const [hidden, setHidden] = useState<string[]>([]);
  const list = useQuery(pdfOperationsQuery);
  const operations = (list.data ?? []).filter(
    (op) => !hidden.includes(op.id) && (shown.includes(op.id) || active(op)),
  );

  // Refetch what a finished operation changed: its new files' folder, and the trash when the originals went there.
  const states = useRef(new Map<string, PdfOperation["state"]>());
  useEffect(() => {
    let finished = false;
    for (const op of list.data ?? []) {
      const before = states.current.get(op.id);
      states.current.set(op.id, op.state);
      if (before && before !== op.state && op.state === "done") finished = true;
    }
    if (finished) void invalidateTrash(queryClient);
  }, [list.data, queryClient]);

  const value: PdfTasks = {
    operations,
    tasks: operations.map((op) => ({
      id: op.id,
      kind: op.kind,
      name: op.kind === "merge" ? (op.names[0] ?? "") : (op.sources[0] ?? ""),
      state: op.state,
      pagesDone: op.pagesDone,
      pagesTotal: op.pagesTotal,
      files: op.state === "done" && op.kind === "split" ? op.results.length : op.kind === "merge" ? op.sources.length : op.names.length,
      duplicates: op.results.filter((r) => r.duplicateOf).length,
      error: op.error ?? undefined,
    })),
    started: (op) => {
      states.current.set(op.id, op.state);
      queryClient.setQueryData(pdfOperationsQuery.queryKey, (old) => [op, ...(old ?? []).filter((o) => o.id !== op.id)]);
      setShown((ids) => [...ids, op.id]);
      void queryClient.invalidateQueries({ queryKey: pdfOperationsQuery.queryKey });
    },
    cancel: async (id) => {
      const op = await pdf.cancel(id);
      queryClient.setQueryData(pdfOperationsQuery.queryKey, (old) => (old ?? []).map((o) => (o.id === id ? op : o)));
    },
    dismiss: (id) => setHidden((ids) => [...ids, id]),
    clear: () => setHidden((ids) => [...ids, ...operations.filter((op) => !active(op)).map((op) => op.id)]),
    removeCopies: async (id) => {
      const op = operations.find((o) => o.id === id);
      const copies = op?.results.filter((r) => r.duplicateOf).map((r) => r.nodeId) ?? [];
      if (copies.length > 0) await drive.trash(copies);
      setHidden((ids) => [...ids, id]);
      await invalidateTrash(queryClient);
      return copies.length;
    },
  };

  return <PdfTasksContext value={value}>{children}</PdfTasksContext>;
}

