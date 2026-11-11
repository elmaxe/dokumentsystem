import type { NodeDto, TrashItem } from "@dokument/api";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { ActivityIndicator, View } from "react-native";
import { useFormat } from "../../src/state/format";
import { deleteForever, emptyTrash, restore } from "../../src/state/changes";
import { kindOfNode, trashQuery } from "../../src/state/queries";
import { useTheme } from "../../src/state/theme";
import { Button } from "../../src/ui/Button";
import { EmptyState } from "../../src/ui/EmptyState";
import { FileList } from "../../src/ui/FileList";
import { FileTypeIcon } from "../../src/ui/FileTypeIcon";
import { Icon } from "../../src/ui/Icon";
import { Notice } from "../../src/ui/Notice";
import { Sheet, SheetAction, useKept } from "../../src/ui/Sheet";
import { Text } from "../../src/ui/Text";
import { TopBar } from "../../src/ui/TopBar";

const day = 24 * 60 * 60 * 1000;
/** From this many days left, the countdown turns to a warning, as on the web. */
const soonDays = 3;

/**
 * The person's trash (docs/design.md §12, §15.3): what's in it, where it came from and when it goes. A tap opens an
 * item's actions, Restore or Delete forever; Empty trash asks first, with how much it frees.
 */
export default function Trash() {
  const { t } = useTranslation();
  const theme = useTheme();
  const format = useFormat();
  const queryClient = useQueryClient();
  const trash = useQuery(trashQuery);
  const [chosen, setChosen] = useState<TrashItem | null>(null);
  // Shown while the sheet slides away after it was set back to null.
  const shownItem = useKept(chosen);
  const [confirm, setConfirm] = useState<
    { kind: "delete"; item: TrashItem } | { kind: "empty" } | null
  >(null);
  const shownConfirm = useKept(confirm);
  const [busy, setBusy] = useState(false);
  const items = trash.data?.items ?? [];
  const byId = new Map(items.map((item) => [item.node.id, item]));
  const keepDays = trash.data?.keepDays ?? 30;
  // When the page opened: the countdowns change by the day, not while it is open.
  const [now] = useState(() => Date.now());
  const daysLeft = (item: TrashItem) =>
    Math.max(0, Math.ceil((Date.parse(item.deleteAt) - now) / day));

  const meta = (node: NodeDto) => {
    const item = byId.get(node.id);
    if (!item) return "";
    if (item.trashedBy) return t("trash.trashedBy", { name: item.trashedBy });
    const days = daysLeft(item);
    return days === 0 ? t("trash.deletesToday") : t("trash.deletesIn", { count: days });
  };
  const open = (node: NodeDto) => setChosen(byId.get(node.id) ?? null);

  const run = async (work: () => Promise<void>) => {
    setBusy(true);
    await work();
    setBusy(false);
    setConfirm(null);
  };

  const confirmTitle =
    shownConfirm?.kind === "delete"
      ? t("trash.delete.titleOne", { name: shownConfirm.item.node.name })
      : shownConfirm?.kind === "empty"
        ? t("trash.empty.title")
        : undefined;
  const confirmText =
    shownConfirm?.kind === "delete"
      ? [
          shownConfirm.item.node.kind === "folder" && shownConfirm.item.itemCount > 0
            ? t("trash.delete.inside", { count: shownConfirm.item.itemCount })
            : null,
          t("trash.cantUndo"),
        ]
          .filter(Boolean)
          .join(" ")
      : shownConfirm?.kind === "empty"
        ? `${t("trash.empty.text", { count: items.length, size: format.size(trash.data?.freeableBytes ?? 0) })} ${t("trash.cantUndo")}`
        : "";

  return (
    <View className="flex-1 bg-surface">
      <TopBar
        title={t("nav.trash")}
        back
        actions={
          items.length > 0 ? (
            <Button
              variant="text"
              label={t("files.emptyTrash")}
              onPress={() => setConfirm({ kind: "empty" })}
            />
          ) : null
        }
      />
      {trash.isPending ? (
        <View className="flex-1 items-center justify-center">
          <ActivityIndicator color={theme.color("primary")} />
        </View>
      ) : trash.isError && !trash.data ? (
        <View className="p-4">
          <Notice tone="error" icon="error" text={t("errors.generic")} />
        </View>
      ) : (
        <FileList
          nodes={items.map((item) => item.node)}
          view="list"
          keepOrder
          meta={meta}
          onOpenFolder={open}
          onOpenFile={open}
          onMore={open}
          refreshing={trash.isRefetching}
          onRefresh={() => void trash.refetch()}
          header={
            items.length > 0 ? (
              <View className="mb-2">
                <Notice icon="info" text={t("files.trashNote", { count: keepDays })} />
              </View>
            ) : undefined
          }
          empty={
            <EmptyState
              icon="delete"
              title={t("files.empty.trashTitle")}
              text={t("files.empty.trashText", { count: keepDays })}
            />
          }
        />
      )}
      <Sheet open={!!chosen} onClose={() => setChosen(null)}>
        {shownItem ? (
          <>
            <View className="mx-5 mb-2 flex-row items-center gap-3">
              <FileTypeIcon kind={kindOfNode(shownItem.node)} />
              <View className="min-w-0 flex-1">
                <Text variant="title-md" numberOfLines={2} className="text-on-surface">
                  {shownItem.node.name}
                </Text>
                <Text
                  variant="body-sm"
                  className={
                    daysLeft(shownItem) <= soonDays ? "text-warning" : "text-on-surface-variant"
                  }
                >
                  {t("trash.meta", {
                    folder: shownItem.path.at(-1)?.name ?? t("nav.myFiles"),
                    count: daysLeft(shownItem),
                  })}
                </Text>
              </View>
            </View>
            <SheetAction
              icon={<Icon name="restore_from_trash" />}
              label={t("actions.restore")}
              onPress={() => {
                const item = shownItem;
                setChosen(null);
                void restore(t, queryClient, [item.node]);
              }}
            />
            <SheetAction
              icon={<Icon name="delete_forever" color="error" />}
              label={t("actions.deleteForever")}
              danger
              onPress={() => {
                const item = shownItem;
                setChosen(null);
                setConfirm({ kind: "delete", item });
              }}
            />
          </>
        ) : null}
      </Sheet>
      <Sheet open={!!confirm} onClose={() => setConfirm(null)} title={confirmTitle}>
        <View className="mx-5 gap-4">
          <Text className="text-on-surface-variant">{confirmText}</Text>
          <View className="flex-row justify-end gap-2">
            <Button variant="text" label={t("common.cancel")} onPress={() => setConfirm(null)} />
            <Button
              variant="danger"
              icon="delete_forever"
              busy={busy}
              label={confirm?.kind === "empty" ? t("files.emptyTrash") : t("actions.deleteForever")}
              onPress={() =>
                void run(() =>
                  confirm?.kind === "delete"
                    ? deleteForever(t, queryClient, [confirm.item.node.id])
                    : emptyTrash(t, queryClient),
                )
              }
            />
          </View>
        </View>
      </Sheet>
    </View>
  );
}
