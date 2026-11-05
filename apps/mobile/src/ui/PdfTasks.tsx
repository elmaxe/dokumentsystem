import type { PdfOperation } from "@dokument/api";
import { useQueryClient } from "@tanstack/react-query";
import { router } from "expo-router";
import { useEffect } from "react";
import { useTranslation } from "react-i18next";
import { View } from "react-native";
import { dismissPdf, isActive, pdf, pdfOperationsQuery, usePdfTasks } from "../state/pdf";
import { drive, nodeQuery } from "../state/queries";
import { Button, IconButton } from "./Button";
import { Icon } from "./Icon";
import { Text } from "./Text";

/** The merges and splits started on this phone, above the Scan button like uploads (docs/design.md §8.4). */
export function PdfTaskRows() {
  const tasks = usePdfTasks();
  return (
    <>
      {tasks.map((op) => (
        <PdfTaskRow key={op.id} op={op} />
      ))}
    </>
  );
}

function PdfTaskRow({ op }: { op: PdfOperation }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const busy = isActive(op);
  const duplicates = op.results.filter((r) => r.duplicateOf).length;
  const done = op.state === "done";
  const failed = op.state === "failed";
  const name = op.kind === "merge" ? (op.names[0] ?? "") : (op.sources[0] ?? "");

  // A finished one without anything to decide hides itself after a moment, as uploads do.
  useEffect(() => {
    if (!done || duplicates > 0) return;
    const timer = setTimeout(() => dismissPdf(op.id), 6_000);
    return () => clearTimeout(timer);
  }, [done, duplicates, op.id]);

  const status = (() => {
    switch (op.state) {
      case "waiting":
        return t("upload.task.waiting");
      case "working":
        return op.kind === "merge"
          ? t("upload.task.merging", { done: op.pagesDone, total: op.pagesTotal })
          : t("upload.task.splitting", { done: op.pagesDone, total: op.pagesTotal });
      case "saving":
        return t("upload.task.saving");
      case "done": {
        const made =
          op.kind === "merge"
            ? t("upload.task.merged", { count: op.sources.length })
            : t("upload.task.splitDone", { count: op.results.length });
        return duplicates ? `${made} · ${t("upload.task.duplicates", { count: duplicates })}` : made;
      }
      case "failed":
        return op.error ?? t("upload.task.failed");
      case "cancelled":
        return t("upload.task.cancelled");
    }
  })();

  const open = () => {
    const first = op.results[0];
    if (!first) return;
    dismissPdf(op.id);
    if (op.kind === "merge") return router.push({ pathname: "/file/[id]", params: { id: first.nodeId } });
    // A split may have made a folder for its files, so it's the new files' folder that opens.
    void queryClient
      .fetchQuery(nodeQuery(first.nodeId))
      .then(({ node }) =>
        node.parentId ? router.push({ pathname: "/folder/[id]", params: { id: node.parentId } }) : router.navigate("/"),
      )
      .catch(() => {});
  };

  const removeCopies = async () => {
    const copies = op.results.filter((r) => r.duplicateOf).map((r) => r.nodeId);
    dismissPdf(op.id);
    await drive.trash(copies).catch(() => {});
    void queryClient.invalidateQueries({ queryKey: ["folder"] });
  };

  const percent = op.pagesTotal > 0 ? Math.floor((op.pagesDone / op.pagesTotal) * 100) : 0;
  return (
    <View accessibilityLiveRegion="polite" className="w-full gap-2 rounded-md bg-surface-container-high px-4 py-3 shadow-elevation-2">
      <View className="flex-row items-center gap-3">
        <Icon
          name={done ? (duplicates ? "content_copy" : "check_circle") : failed ? "error" : op.kind === "merge" ? "call_merge" : "call_split"}
          color={done ? (duplicates ? "warning" : "success") : failed ? "error" : "on-surface-variant"}
          filled={done || failed}
        />
        <View className="min-w-0 flex-1">
          <Text variant="title-sm" numberOfLines={1} className="text-on-surface">
            {name}
          </Text>
          <Text variant="body-sm" className={failed ? "text-error" : "text-on-surface-variant"}>
            {status}
          </Text>
        </View>
        {done && duplicates ? <Button variant="text" label={t("upload.removeCopy")} onPress={() => void removeCopies()} /> : null}
        {done && !duplicates && op.results.length > 0 ? (
          <Button variant="text" label={t("actions.open")} onPress={open} />
        ) : null}
        {busy && op.state !== "saving" ? (
          <IconButton
            icon="close"
            compact
            label={t("mobile.pdf.cancel", { name })}
            onPress={() =>
              void pdf
                .cancel(op.id)
                .then((next) =>
                  queryClient.setQueryData(pdfOperationsQuery.queryKey, (old) => (old ?? []).map((o) => (o.id === op.id ? next : o))),
                )
                .catch(() => {})
            }
          />
        ) : !busy ? (
          <IconButton icon="close" compact label={t("mobile.uploads.hide", { name })} onPress={() => dismissPdf(op.id)} />
        ) : null}
      </View>
      {busy ? (
        <View className="h-1 overflow-hidden rounded-full bg-surface-container-highest">
          <View style={{ width: `${op.state === "working" ? percent : op.state === "saving" ? 100 : 0}%` }} className="h-1 rounded-full bg-primary" />
        </View>
      ) : null}
    </View>
  );
}
