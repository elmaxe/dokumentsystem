import type { NodeDto } from "@dokument/api";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { ActivityIndicator, FlatList, Pressable, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { canEdit, createFolder } from "../state/changes";
import { folderQuery, sessionQuery } from "../state/queries";
import { useTheme } from "../state/theme";
import { Button, IconButton } from "./Button";
import { FileTypeIcon } from "./FileTypeIcon";
import { Icon } from "./Icon";
import { NameSheet } from "./NameSheet";
import { Notice } from "./Notice";
import { Text } from "./Text";

export type PickedFolder = { id: string | null; name: string; path: string };

/**
 * A full-screen folder picker (docs/design.md §15.3), for moving items and for where shared files go. It lists only
 * folders, with a way up and a way to make a new one; folders the person can only view can't be picked. In someone
 * else's folder it goes up only as far as the top folder shared with the person, as their path does (§9).
 */
export function FolderPicker({
  title,
  start,
  submitLabel,
  busy,
  unavailable,
  blocked,
  onCancel,
  onSubmit,
  children,
}: {
  title: string;
  start: string | null;
  submitLabel: string;
  busy?: boolean;
  /** Folders that can't be opened, such as the ones being moved. */
  unavailable?: Set<string>;
  /** Whether the open folder can't be picked, such as the one the items are in already. */
  blocked?: (folderId: string | null) => boolean;
  onCancel: () => void;
  onSubmit: (folder: PickedFolder) => void;
  /** Sheets that belong to the picker's caller, such as a question before the move. */
  children?: ReactNode;
}) {
  const { t } = useTranslation();
  const theme = useTheme();
  const insets = useSafeAreaInsets();
  const queryClient = useQueryClient();
  const [folderId, setFolderId] = useState<string | null>(start);
  const listing = useQuery(folderQuery(folderId));
  // The name sheet stays mounted while it slides away; a new key starts it empty next time.
  const [naming, setNaming] = useState({ key: 0, open: false });

  const folders = (listing.data?.items ?? []).filter((item) => item.kind === "folder");
  const path = listing.data?.path ?? [];
  const here = listing.data?.folder ?? null;
  const me = useQuery(sessionQuery).data?.user.id;
  const theirs = !!here && !!me && here.ownerId !== me;
  const root = theirs ? t("nav.sharedWithMe") : t("nav.myFiles");
  const parentName = path.length > 1 ? path[path.length - 2]!.name : root;
  // At the top folder shared with the person, what holds it is out of sight.
  const canGoUp = folderId !== null && !(theirs && path.length <= 1);
  const editable = folderId === null || canEdit(here?.access);
  const names = [root, ...path.map((entry) => entry.name)];
  const disabled =
    busy || listing.isPending || !editable || !!blocked?.(folderId) || (folderId !== null && !!unavailable?.has(folderId));

  return (
    <View className="flex-1 bg-surface" style={{ paddingTop: insets.top }}>
      <View className="h-topbar flex-row items-center gap-1 pl-1 pr-2">
        <IconButton icon="close" label={t("common.cancel")} onPress={onCancel} color="on-surface" />
        <Text variant="title-lg" numberOfLines={1} accessibilityRole="header" className="flex-1 text-on-surface">
          {title}
        </Text>
        {editable ? (
          <IconButton
            icon="create_new_folder"
            label={t("dialogs.newFolder.title")}
            onPress={() => setNaming((n) => ({ key: n.key + 1, open: true }))}
            color="on-surface"
          />
        ) : null}
      </View>
      <View className="flex-row items-center gap-1 px-4 pb-2">
        <Icon name="folder" size={18} />
        <Text variant="body-md" numberOfLines={1} className="flex-1 text-on-surface-variant">
          {names.join(" / ")}
        </Text>
      </View>
      {listing.isPending ? (
        <View className="flex-1 items-center justify-center">
          <ActivityIndicator color={theme.color("primary")} />
        </View>
      ) : (
        <FlatList
          data={folders}
          keyExtractor={(folder) => folder.id}
          ListHeaderComponent={
            canGoUp ? (
              <Pressable
                accessibilityRole="button"
                onPress={() => setFolderId(here?.parentId ?? null)}
                className="min-h-14 flex-row items-center gap-4 px-4 active:bg-surface-container-highest"
              >
                <Icon name="arrow_upward" />
                <Text className="flex-1 text-on-surface">{t("dialogs.move.up", { folder: parentName })}</Text>
              </Pressable>
            ) : undefined
          }
          ListEmptyComponent={
            <Text variant="body-md" className="py-8 text-center text-on-surface-variant">
              {t("dialogs.move.noFolders")}
            </Text>
          }
          renderItem={({ item }) => (
            <FolderRow folder={item} unavailable={!!unavailable?.has(item.id)} onOpen={() => setFolderId(item.id)} />
          )}
        />
      )}
      <View style={{ paddingBottom: insets.bottom + 12 }} className="gap-3 bg-surface-container px-4 pt-3">
        {!editable && !listing.isPending ? <Notice tone="warning" icon="block" text={t("mobile.move.viewOnly")} /> : null}
        <View className="flex-row justify-end gap-2">
          <Button variant="text" label={t("common.cancel")} onPress={onCancel} />
          <Button
            label={submitLabel}
            busy={busy}
            disabled={disabled}
            onPress={() =>
              onSubmit({ id: folderId, name: here?.name ?? root, path: names.slice(0, -1).join(" / ") })
            }
          />
        </View>
      </View>
      {naming.key > 0 ? (
        <NameSheet
          key={naming.key}
          open={naming.open}
          onClose={() => setNaming((n) => ({ ...n, open: false }))}
          title={t("dialogs.newFolder.title")}
          label={t("dialogs.newFolder.label")}
          submitLabel={t("dialogs.newFolder.create")}
          initial=""
          onSubmit={(name) => createFolder(t, queryClient, folderId, name)}
        />
      ) : null}
      {children}
    </View>
  );
}

function FolderRow({ folder, unavailable, onOpen }: { folder: NodeDto; unavailable: boolean; onOpen: () => void }) {
  const { t } = useTranslation();
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={t("dialogs.move.open", { name: folder.name })}
      accessibilityState={{ disabled: unavailable }}
      disabled={unavailable}
      onPress={onOpen}
      className={`min-h-14 flex-row items-center gap-4 px-4 active:bg-surface-container-highest ${unavailable ? "opacity-40" : ""}`}
    >
      <FileTypeIcon kind="folder" />
      <Text numberOfLines={1} className="flex-1 text-on-surface">
        {folder.name}
      </Text>
      {unavailable ? null : <Icon name="chevron_right" />}
    </Pressable>
  );
}
