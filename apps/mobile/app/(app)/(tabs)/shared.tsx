import type { NodeDto, SharedItem } from "@dokument/api";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { router } from "expo-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { FlatList, Pressable, RefreshControl, View } from "react-native";
import { useFormat } from "../../../src/state/format";
import { sharedQuery, sharing } from "../../../src/state/sharing";
import { useTheme } from "../../../src/state/theme";
import { IconButton } from "../../../src/ui/Button";
import { EmptyState } from "../../../src/ui/EmptyState";
import { openFile, Thumbnail } from "../../../src/ui/FileList";
import { ItemSheet } from "../../../src/ui/ItemSheet";
import { AccountButton } from "../../../src/ui/ListActions";
import { Notice } from "../../../src/ui/Notice";
import { Text } from "../../../src/ui/Text";
import { TopBar } from "../../../src/ui/TopBar";

/**
 * Shared with me (docs/design.md §9, §15.3): the top-most things shared with the person, newest first, with who
 * shared each, when, and what the person may do. Unopened ones say New; opening one marks it seen.
 */
export default function Shared() {
  const { t } = useTranslation();
  const theme = useTheme();
  const queryClient = useQueryClient();
  const shared = useQuery(sharedQuery);
  const [selected, setSelected] = useState<NodeDto | null>(null);

  const seen = (item: SharedItem) => {
    if (!item.new) return;
    void sharing
      .seen(item.node.id)
      .then(() => queryClient.invalidateQueries({ queryKey: sharedQuery.queryKey }))
      .catch(() => {
        // It stays New until the next time it's opened.
      });
  };
  const openFolder = (node: NodeDto) =>
    router.push({ pathname: "/folder/[id]", params: { id: node.id } });
  const open = (item: SharedItem) => {
    seen(item);
    if (item.node.kind === "folder") openFolder(item.node);
    else openFile(item.node);
  };

  return (
    <View className="flex-1 bg-surface">
      <TopBar title={t("nav.sharedWithMe")} actions={<AccountButton />} />
      {shared.isError && !shared.data ? (
        <View className="p-4">
          <Notice tone="error" icon="error" text={t("errors.generic")} />
        </View>
      ) : (
        <FlatList
          data={shared.data ?? []}
          keyExtractor={(item) => item.node.id}
          contentContainerClassName="pb-6 pt-2"
          refreshControl={
            <RefreshControl
              refreshing={shared.isRefetching}
              onRefresh={() => void shared.refetch()}
              tintColor={theme.color("primary")}
            />
          }
          ListEmptyComponent={
            shared.isPending ? undefined : (
              <EmptyState
                icon="folder_shared"
                title={t("files.empty.sharedTitle")}
                text={t("files.empty.sharedText")}
              />
            )
          }
          renderItem={({ item }) => (
            <SharedRow
              item={item}
              onOpen={() => open(item)}
              onMore={() => setSelected(item.node)}
            />
          )}
        />
      )}
      <ItemSheet
        node={selected}
        onClose={() => setSelected(null)}
        onOpenFolder={openFolder}
        topShared
      />
    </View>
  );
}

function SharedRow({
  item,
  onOpen,
  onMore,
}: {
  item: SharedItem;
  onOpen: () => void;
  onMore: () => void;
}) {
  const { t } = useTranslation();
  const format = useFormat();
  const meta = [
    item.node.ownerName,
    format.date(item.sharedAt),
    item.role === "editor" ? t("mobile.sharing.canEdit") : t("mobile.sharing.canView"),
  ].join(" · ");
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={item.new ? `${item.node.name}, ${t("files.new")}` : item.node.name}
      accessibilityHint={meta}
      onPress={onOpen}
      onLongPress={onMore}
      className="min-h-16 flex-row items-center gap-4 py-2 pl-4 pr-1 active:bg-surface-container-high"
    >
      <Thumbnail node={item.node} size="small" className="h-10 w-10 rounded-sm" />
      <View className="min-w-0 flex-1">
        <View className="flex-row items-center gap-2">
          <Text numberOfLines={1} className="shrink text-on-surface">
            {item.node.name}
          </Text>
          {item.new ? (
            <View className="rounded-xs bg-primary px-1.5">
              <Text variant="label-sm" className="text-on-primary">
                {t("files.new")}
              </Text>
            </View>
          ) : null}
        </View>
        <Text variant="body-sm" numberOfLines={1} className="text-on-surface-variant">
          {meta}
        </Text>
      </View>
      <IconButton
        icon="more_vert"
        label={t("files.moreActionsFor", { name: item.node.name })}
        onPress={onMore}
      />
    </Pressable>
  );
}
