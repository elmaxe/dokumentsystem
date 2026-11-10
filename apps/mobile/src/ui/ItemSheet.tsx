import type { NodeDto } from "@dokument/api";
import { useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { ActivityIndicator, View } from "react-native";
import { router } from "expo-router";
import { useFormat } from "../state/format";
import { openInAnotherApp } from "../state/openFile";
import { readablePdf } from "../state/pdf";
import { canEdit, rename, startMove, trash, useCanMove } from "../state/changes";
import { AccessSheet } from "./AccessSheet";
import { drive, invalidateStars, kindOfNode } from "../state/queries";
import { useTheme } from "../state/theme";
import { FileTypeIcon } from "./FileTypeIcon";
import { NameSheet } from "./NameSheet";
import { Icon } from "./Icon";
import { Notice } from "./Notice";
import { Sheet, SheetAction } from "./Sheet";
import { Text } from "./Text";

/**
 * What can be done with one file or folder (docs/design.md §15.3, §16.3). Rename, Move and Move to trash need edit
 * access (§9). The owner shares; everyone else sees who has access, and can leave what was shared with them directly.
 */
export function ItemSheet({
  node,
  onClose,
  onOpenFolder,
  onSelect,
  onTrashed,
  topShared,
}: {
  node: NodeDto | null;
  onClose: () => void;
  onOpenFolder: (node: NodeDto) => void;
  /** Starts selecting files with this one, as a long press does. */
  onSelect?: (node: NodeDto) => void;
  /** Leaves a page that only shows this item, such as the viewer, once it's in the trash or the person left it. */
  onTrashed?: () => void;
  /** Listed in Shared with me: what holds it is out of sight, so it can't be moved or trashed from here, only left. */
  topShared?: boolean;
}) {
  const { t } = useTranslation();
  const theme = useTheme();
  const format = useFormat();
  const queryClient = useQueryClient();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [renaming, setRenaming] = useState<NodeDto | null>(null);
  const [viewing, setViewing] = useState<{ node: NodeDto; leave: boolean } | null>(null);
  const canMove = useCanMove();

  const renameSheet = renaming ? (
    <NameSheet
      key={renaming.id}
      open
      onClose={() => setRenaming(null)}
      title={t("dialogs.rename.title")}
      label={t("dialogs.rename.label")}
      submitLabel={t("dialogs.rename.save")}
      initial={renaming.name}
      onSubmit={(name) => rename(t, queryClient, renaming, name)}
    />
  ) : null;
  const sheets = (
    <>
      {renameSheet}
      {viewing ? (
        <AccessSheet
          key={viewing.node.id}
          node={viewing.node}
          leave={viewing.leave}
          onClose={() => setViewing(null)}
          onLeft={onTrashed}
        />
      ) : null}
    </>
  );

  const close = () => {
    setError(null);
    setBusy(false);
    onClose();
  };

  if (!node) return sheets;
  const kind = kindOfNode(node);
  const isFolder = node.kind === "folder";
  const meta = isFolder
    ? t(`kinds.folder`)
    : t("mobile.item.meta", { kind: t(`kinds.${kind}`), size: format.size(node.size ?? 0) });

  const open = async () => {
    setBusy(true);
    setError(null);
    try {
      await openInAnotherApp(node);
      close();
    } catch {
      setBusy(false);
      setError(t("mobile.item.openFailed"));
    }
  };

  const star = async () => {
    close();
    try {
      await drive.star([node.id], !node.starred);
    } catch {
      // The star shows its old state again after the refetch, so the failure is visible.
    }
    await invalidateStars(queryClient);
  };

  const editable = canEdit(node.access);

  return (
    <>
    <Sheet open onClose={close}>
      <View className="mx-5 mb-2 flex-row items-center gap-3">
        <FileTypeIcon kind={kind} />
        <View className="min-w-0 flex-1">
          <Text variant="title-md" numberOfLines={2} className="text-on-surface">
            {node.name}
          </Text>
          <Text variant="body-sm" className="text-on-surface-variant">
            {meta}
          </Text>
        </View>
      </View>
      {error ? (
        <View className="mx-5 mb-2">
          <Notice tone="error" icon="error" text={error} />
        </View>
      ) : null}
      {busy ? (
        <View className="min-h-touch flex-row items-center gap-4 px-5" accessibilityLiveRegion="polite">
          <ActivityIndicator color={theme.color("primary")} />
          <Text className="text-on-surface">{t("mobile.item.opening", { name: node.name })}</Text>
        </View>
      ) : isFolder ? (
        <SheetAction
          icon={<Icon name="folder_open" />}
          label={t("actions.open")}
          onPress={() => {
            close();
            onOpenFolder(node);
          }}
        />
      ) : (
        <SheetAction icon={<Icon name="open_in_new" />} label={t("mobile.item.openIn")} onPress={() => void open()} />
      )}
      <SheetAction
        icon={<Icon name="info" />}
        label={t("mobile.info.open")}
        onPress={() => {
          close();
          router.push({ pathname: "/info/[id]", params: { id: node.id } });
        }}
      />
      {readablePdf(node) ? (
        <SheetAction
          icon={<Icon name="call_split" />}
          label={t("pdf.split.action")}
          onPress={() => {
            close();
            router.push({ pathname: "/split/[id]", params: { id: node.id } });
          }}
        />
      ) : null}
      {onSelect ? (
        <SheetAction
          icon={<Icon name="check_box_outline_blank" />}
          label={t("mobile.select")}
          onPress={() => {
            close();
            onSelect(node);
          }}
        />
      ) : null}
      <SheetAction
        icon={<Icon name="star" filled={node.starred} />}
        label={node.starred ? t("actions.unstar") : t("actions.star")}
        onPress={() => void star()}
      />
      {node.access === "owner" ? (
        <SheetAction
          icon={<Icon name="person_add" />}
          label={t("actions.share")}
          onPress={() => {
            close();
            router.push({ pathname: "/people/[id]", params: { id: node.id } });
          }}
        />
      ) : (
        <SheetAction
          icon={<Icon name="group" />}
          label={t("share.peopleWithAccess")}
          onPress={() => {
            close();
            setViewing({ node, leave: false });
          }}
        />
      )}
      <SheetAction
        icon={<Icon name="history" />}
        label={t("details.tabs.activity")}
        onPress={() => {
          close();
          router.push({ pathname: "/activity/[id]", params: { id: node.id } });
        }}
      />
      {topShared ? (
        <>
          <View className="my-2 h-px bg-outline-variant" />
          <SheetAction
            icon={<Icon name="person_remove" />}
            label={t("share.leave")}
            onPress={() => {
              close();
              setViewing({ node, leave: true });
            }}
          />
        </>
      ) : editable ? (
        <>
          <View className="my-2 h-px bg-outline-variant" />
          <SheetAction
            icon={<Icon name="drive_file_rename_outline" />}
            label={t("actions.rename")}
            onPress={() => {
              close();
              setRenaming(node);
            }}
          />
          {canMove([node]) ? (
            <SheetAction
              icon={<Icon name="drive_file_move" />}
              label={t("actions.move")}
              onPress={() => {
                close();
                startMove([node]);
              }}
            />
          ) : null}
          <SheetAction
            icon={<Icon name="delete" />}
            label={t("actions.trash")}
            onPress={() => {
              close();
              onTrashed?.();
              void trash(t, queryClient, [node]);
            }}
          />
        </>
      ) : null}
    </Sheet>
    {sheets}
    </>
  );
}
