import type { NodeDto } from "@dokument/api";
import { useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { ActivityIndicator, View } from "react-native";
import { router } from "expo-router";
import { useFormat } from "../state/format";
import { openInAnotherApp } from "../state/openFile";
import { readablePdf } from "../state/pdf";
import { drive, invalidateStars, kindOfNode } from "../state/queries";
import { useTheme } from "../state/theme";
import { FileTypeIcon } from "./FileTypeIcon";
import { Icon } from "./Icon";
import { Notice } from "./Notice";
import { Sheet, SheetAction } from "./Sheet";
import { Text } from "./Text";

/** What can be done with one file or folder (docs/design.md §16.3). More actions arrive with later milestones. */
export function ItemSheet({
  node,
  onClose,
  onOpenFolder,
  onSelect,
}: {
  node: NodeDto | null;
  onClose: () => void;
  onOpenFolder: (node: NodeDto) => void;
  /** Starts selecting files with this one, as a long press does. */
  onSelect?: (node: NodeDto) => void;
}) {
  const { t } = useTranslation();
  const theme = useTheme();
  const format = useFormat();
  const queryClient = useQueryClient();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const close = () => {
    setError(null);
    setBusy(false);
    onClose();
  };

  if (!node) return null;
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

  return (
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
      {onSelect && !isFolder ? (
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
    </Sheet>
  );
}
