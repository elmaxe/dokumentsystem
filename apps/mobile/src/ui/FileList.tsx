import type { NodeDto } from "@dokument/api";
import { isPaged, sortItems } from "@dokument/core";
import { Image } from "expo-image";
import { router } from "expo-router";
import { useMemo, type ReactElement } from "react";
import { useTranslation } from "react-i18next";
import { FlatList, Pressable, RefreshControl, View } from "react-native";
import { useFormat } from "../state/format";
import { useListPrefs } from "../state/prefs";
import { durationOf, kindOfNode, thumbnailSource } from "../state/queries";
import { useTheme } from "../state/theme";
import { IconButton } from "./Button";
import { FileTypeIcon } from "./FileTypeIcon";
import { Icon } from "./Icon";
import { Text } from "./Text";

/** Opens a file's preview full screen. */
export const openFile = (node: NodeDto) =>
  router.push({ pathname: "/file/[id]", params: { id: node.id } });

type Row =
  | { type: "heading"; key: string; label: string }
  | { type: "folders"; key: string; nodes: NodeDto[] }
  | { type: "tiles"; key: string; nodes: NodeDto[] }
  | { type: "file"; key: string; node: NodeDto };

const pairs = (nodes: NodeDto[]) =>
  Array.from({ length: Math.ceil(nodes.length / 2) }, (_, i) => nodes.slice(i * 2, i * 2 + 2));

/**
 * Folders, then files, as a grid or a list (docs/design.md §16.4). Rows are flattened so long folders scroll
 * smoothly. A tap opens a file, a long press or the more button its actions.
 */
export function FileList({
  nodes,
  onOpenFolder,
  onOpenFile,
  onMore,
  refreshing,
  onRefresh,
  header,
  empty,
  meta,
  keepOrder,
  view,
}: {
  nodes: NodeDto[];
  onOpenFolder: (node: NodeDto) => void;
  onOpenFile: (node: NodeDto) => void;
  onMore: (node: NodeDto) => void;
  refreshing?: boolean;
  onRefresh?: () => void;
  header?: ReactElement;
  empty?: ReactElement;
  /** What the second line says, when it isn't the modified date: "You opened today 09:14". */
  meta?: (node: NodeDto) => string;
  /** Keeps the order given, such as most recent first, instead of the chosen sort. */
  keepOrder?: boolean;
  /** A fixed look, such as a list for Recent, whose lines say more than a tile has room for. */
  view?: "grid" | "list";
}) {
  const { t, i18n } = useTranslation();
  const prefs = useListPrefs();
  const theme = useTheme();

  const rows = useMemo<Row[]>(() => {
    const sorted = keepOrder
      ? nodes
      : sortItems(
          nodes.map((node) => ({
            node,
            name: node.name,
            kind: kindOfNode(node),
            modified: node.modifiedAt,
            size: node.size ?? undefined,
          })),
          prefs.sort,
          i18n.language,
        ).map((item) => item.node);
    const folders = sorted.filter((node) => node.kind === "folder");
    const files = sorted.filter((node) => node.kind !== "folder");
    return [
      ...(folders.length
        ? [
            { type: "heading", key: "h-folders", label: t("files.folders") } as Row,
            ...pairs(folders).map((pair): Row => ({
              type: "folders",
              key: `f-${pair[0]!.id}`,
              nodes: pair,
            })),
          ]
        : []),
      ...(files.length
        ? [
            { type: "heading", key: "h-files", label: t("files.files") } as Row,
            ...((view ?? prefs.view) === "grid"
              ? pairs(files).map((pair): Row => ({
                  type: "tiles",
                  key: `t-${pair[0]!.id}`,
                  nodes: pair,
                }))
              : files.map((node): Row => ({ type: "file", key: node.id, node }))),
          ]
        : []),
    ];
  }, [nodes, keepOrder, view, prefs, i18n.language, t]);

  return (
    <FlatList
      data={rows}
      keyExtractor={(row) => row.key}
      contentContainerClassName="px-4 pb-28"
      ListHeaderComponent={header}
      ListEmptyComponent={empty}
      refreshControl={
        onRefresh ? (
          <RefreshControl
            refreshing={!!refreshing}
            onRefresh={onRefresh}
            tintColor={theme.color("primary")}
          />
        ) : undefined
      }
      renderItem={({ item: row }) => {
        switch (row.type) {
          case "heading":
            return (
              <Text
                variant="title-sm"
                accessibilityRole="header"
                className="mb-2 mt-4 text-on-surface-variant"
              >
                {row.label}
              </Text>
            );
          case "folders":
            return (
              <View className="mb-3 flex-row gap-3">
                {row.nodes.map((node) => (
                  <FolderTile
                    key={node.id}
                    node={node}
                    onPress={() => onOpenFolder(node)}
                    onLongPress={() => onMore(node)}
                  />
                ))}
                {row.nodes.length === 1 ? <View className="flex-1" /> : null}
              </View>
            );
          case "tiles":
            return (
              <View className="mb-3 flex-row gap-3">
                {row.nodes.map((node) => (
                  <FileTile
                    key={node.id}
                    node={node}
                    meta={meta}
                    onPress={() => onOpenFile(node)}
                    onMore={() => onMore(node)}
                  />
                ))}
                {row.nodes.length === 1 ? <View className="flex-1" /> : null}
              </View>
            );
          case "file":
            return (
              <FileRow
                node={row.node}
                meta={meta}
                onPress={() => onOpenFile(row.node)}
                onMore={() => onMore(row.node)}
              />
            );
        }
      }}
    />
  );
}

function FolderTile({
  node,
  onPress,
  onLongPress,
}: {
  node: NodeDto;
  onPress: () => void;
  onLongPress: () => void;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={node.name}
      onPress={onPress}
      onLongPress={onLongPress}
      className="h-14 min-w-0 flex-1 flex-row items-center gap-3 rounded-md border border-outline-variant bg-surface-container-lowest px-3 active:bg-surface-container-high"
    >
      <FileTypeIcon kind="folder" />
      <Text numberOfLines={1} className="flex-1 text-on-surface">
        {node.name}
      </Text>
      {node.starred ? <Icon name="star" filled size={16} /> : null}
    </Pressable>
  );
}

function useMeta(meta?: (node: NodeDto) => string) {
  const format = useFormat();
  const { t } = useTranslation();
  return (node: NodeDto) => {
    if (meta) return meta(node);
    const parts = [format.date(node.modifiedAt)];
    const duration = durationOf(node);
    if (duration) parts.push(duration);
    if (node.processing.length > 0) parts.push(t("processing.badge"));
    return parts.join(" · ");
  };
}

function Thumbnail({
  node,
  size,
  className,
}: {
  node: NodeDto;
  size: "small" | "medium";
  className: string;
}) {
  const source = thumbnailSource(node, size);
  const kind = kindOfNode(node);
  return (
    <View
      className={`items-center justify-center overflow-hidden bg-surface-container-high ${className}`}
    >
      {source ? (
        <Image
          source={source}
          style={{ width: "100%", height: "100%" }}
          contentFit="cover"
          // Paged files show the top of page 1, as a reader would see it (§16.4).
          contentPosition={isPaged({ kind }) ? "top" : "center"}
          cachePolicy="disk"
          recyclingKey={source.cacheKey}
          transition={120}
          accessible={false}
        />
      ) : (
        <FileTypeIcon kind={kind} size={size === "medium" ? 40 : 24} />
      )}
    </View>
  );
}

function FileTile({
  node,
  meta,
  onPress,
  onMore,
}: {
  node: NodeDto;
  meta?: (node: NodeDto) => string;
  onPress: () => void;
  onMore: () => void;
}) {
  const { t } = useTranslation();
  const metaOf = useMeta(meta);
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={node.name}
      accessibilityHint={metaOf(node)}
      onPress={onPress}
      onLongPress={onMore}
      className="min-w-0 flex-1 overflow-hidden rounded-md border border-outline-variant bg-surface-container-lowest active:opacity-80"
    >
      <Thumbnail node={node} size="medium" className="h-32" />
      <View className="flex-row items-center gap-1.5 py-2 pl-2.5 pr-1">
        <FileTypeIcon kind={kindOfNode(node)} size={20} />
        <View className="min-w-0 flex-1">
          <View className="flex-row items-center gap-0.5">
            <Text variant="body-md" numberOfLines={1} className="shrink text-on-surface">
              {node.name}
            </Text>
            {node.starred ? <Icon name="star" filled size={14} /> : null}
          </View>
          <Text variant="body-sm" numberOfLines={1} className="text-on-surface-variant">
            {metaOf(node)}
          </Text>
        </View>
        <IconButton
          icon="more_vert"
          label={t("files.moreActionsFor", { name: node.name })}
          onPress={onMore}
          compact
        />
      </View>
    </Pressable>
  );
}

function FileRow({
  node,
  meta,
  onPress,
  onMore,
}: {
  node: NodeDto;
  meta?: (node: NodeDto) => string;
  onPress: () => void;
  onMore: () => void;
}) {
  const { t } = useTranslation();
  const metaOf = useMeta(meta);
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={node.name}
      accessibilityHint={metaOf(node)}
      onPress={onPress}
      onLongPress={onMore}
      className="-mx-4 min-h-16 flex-row items-center gap-3 py-2 pl-4 pr-1 active:bg-surface-container-high"
    >
      <Thumbnail node={node} size="small" className="h-10 w-10 rounded-sm" />
      <View className="min-w-0 flex-1">
        <View className="flex-row items-center gap-0.5">
          <Text numberOfLines={1} className="shrink text-on-surface">
            {node.name}
          </Text>
          {node.starred ? <Icon name="star" filled size={14} /> : null}
        </View>
        <Text variant="body-sm" numberOfLines={1} className="text-on-surface-variant">
          {metaOf(node)}
        </Text>
      </View>
      <IconButton
        icon="more_vert"
        label={t("files.moreActionsFor", { name: node.name })}
        onPress={onMore}
      />
    </Pressable>
  );
}
