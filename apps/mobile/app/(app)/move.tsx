import type { NodeDto, Person } from "@dokument/api";
import { useQueries, useQuery, useQueryClient } from "@tanstack/react-query";
import { router, useLocalSearchParams } from "expo-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { ActivityIndicator, FlatList, Pressable, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { canEdit, createFolder, move } from "../../src/state/changes";
import { drive, folderQuery, nodeQuery } from "../../src/state/queries";
import { useTheme } from "../../src/state/theme";
import { Button, IconButton } from "../../src/ui/Button";
import { FileTypeIcon } from "../../src/ui/FileTypeIcon";
import { Icon } from "../../src/ui/Icon";
import { NameSheet } from "../../src/ui/NameSheet";
import { Notice } from "../../src/ui/Notice";
import { Sheet } from "../../src/ui/Sheet";
import { Text } from "../../src/ui/Text";

/**
 * Picks a folder to move files and folders to (docs/design.md §15.3), starting where the first one is. It lists only
 * folders; the ones being moved, and folders the person can only view, can't be picked. Before a move that would hide
 * something from people who see it now, it asks (§9).
 */
export default function Move() {
  const { t } = useTranslation();
  const theme = useTheme();
  const insets = useSafeAreaInsets();
  const queryClient = useQueryClient();
  const { ids = "", from } = useLocalSearchParams<{ ids?: string; from?: string }>();
  const start = from || null;
  const queries = useQueries({
    queries: ids
      .split(",")
      .filter(Boolean)
      .map((id) => nodeQuery(id)),
  });
  const nodes = queries.every((query) => query.data)
    ? queries.map((query) => query.data!.node)
    : [];
  const [folderId, setFolderId] = useState<string | null>(start);
  const listing = useQuery(folderQuery(folderId));
  const [busy, setBusy] = useState(false);
  const [naming, setNaming] = useState(0);
  const [losing, setLosing] = useState<Person[] | null>(null);

  const moving = new Set(nodes.map((node) => node.id));
  const folders = (listing.data?.items ?? []).filter((item) => item.kind === "folder");
  const path = listing.data?.path ?? [];
  const here = listing.data?.folder ?? null;
  const parentName = path.length > 1 ? path[path.length - 2]!.name : t("nav.myFiles");
  const editable = folderId === null || canEdit(here?.access);
  const alreadyThere =
    nodes.length > 0 && nodes.every((node) => (node.parentId ?? null) === folderId);
  const disabled =
    busy ||
    nodes.length === 0 ||
    !editable ||
    alreadyThere ||
    (folderId !== null && moving.has(folderId));
  const title = t("dialogs.move.title", {
    count: ids.split(",").filter(Boolean).length,
    name: nodes[0]?.name ?? "",
  });

  const finish = async () => {
    setBusy(true);
    setLosing(null);
    await move(t, queryClient, nodes, folderId);
    router.back();
  };

  const moveHere = async () => {
    setBusy(true);
    const people = await drive.losingAccess([...moving], folderId).catch(() => []);
    if (people.length > 0) {
      setBusy(false);
      setLosing(people);
    } else await finish();
  };

  return (
    <View className="flex-1 bg-surface" style={{ paddingTop: insets.top }}>
      <View className="h-topbar flex-row items-center gap-1 pl-1 pr-2">
        <IconButton
          icon="close"
          label={t("common.cancel")}
          onPress={() => router.back()}
          color="on-surface"
        />
        <Text
          variant="title-lg"
          numberOfLines={1}
          accessibilityRole="header"
          className="flex-1 text-on-surface"
        >
          {title}
        </Text>
        {editable ? (
          <IconButton
            icon="create_new_folder"
            label={t("dialogs.newFolder.title")}
            onPress={() => setNaming((n) => n + 1)}
            color="on-surface"
          />
        ) : null}
      </View>
      <View className="flex-row items-center gap-1 px-4 pb-2">
        <Icon name="folder" size={18} />
        <Text variant="body-md" numberOfLines={1} className="flex-1 text-on-surface-variant">
          {[t("nav.myFiles"), ...path.map((entry) => entry.name)].join(" / ")}
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
            folderId !== null ? (
              <Pressable
                accessibilityRole="button"
                onPress={() => setFolderId(here?.parentId ?? null)}
                className="min-h-14 flex-row items-center gap-4 px-4 active:bg-surface-container-highest"
              >
                <Icon name="arrow_upward" />
                <Text className="flex-1 text-on-surface">
                  {t("dialogs.move.up", { folder: parentName })}
                </Text>
              </Pressable>
            ) : undefined
          }
          ListEmptyComponent={
            <Text variant="body-md" className="py-8 text-center text-on-surface-variant">
              {t("dialogs.move.noFolders")}
            </Text>
          }
          renderItem={({ item }) => (
            <FolderRow
              folder={item}
              moving={moving.has(item.id)}
              onOpen={() => setFolderId(item.id)}
            />
          )}
        />
      )}
      <View
        style={{ paddingBottom: insets.bottom + 12 }}
        className="gap-3 bg-surface-container px-4 pt-3"
      >
        {!editable && !listing.isPending ? (
          <Notice tone="warning" icon="block" text={t("mobile.move.viewOnly")} />
        ) : null}
        <View className="flex-row justify-end gap-2">
          <Button variant="text" label={t("common.cancel")} onPress={() => router.back()} />
          <Button
            label={t("dialogs.move.here")}
            busy={busy}
            disabled={disabled}
            onPress={() => void moveHere()}
          />
        </View>
      </View>
      {naming > 0 ? (
        <NameSheet
          key={naming}
          open
          onClose={() => setNaming(0)}
          title={t("dialogs.newFolder.title")}
          label={t("dialogs.newFolder.label")}
          submitLabel={t("dialogs.newFolder.create")}
          initial=""
          onSubmit={(name) => createFolder(t, queryClient, folderId, name)}
        />
      ) : null}
      <Sheet
        open={losing !== null}
        onClose={() => setLosing(null)}
        title={t("dialogs.accessLost.title")}
      >
        <View className="mx-5 gap-4">
          <Text className="text-on-surface-variant">
            {losing
              ? t("dialogs.accessLost.text", {
                  count: nodes.length,
                  // Hermes has no Intl.ListFormat.
                  names: losing.map((person) => person.name).join(", "),
                })
              : ""}
          </Text>
          <View className="flex-row justify-end gap-2">
            <Button variant="text" label={t("common.cancel")} onPress={() => setLosing(null)} />
            <Button label={t("dialogs.accessLost.move")} onPress={() => void finish()} />
          </View>
        </View>
      </Sheet>
    </View>
  );
}

function FolderRow({
  folder,
  moving,
  onOpen,
}: {
  folder: NodeDto;
  moving: boolean;
  onOpen: () => void;
}) {
  const { t } = useTranslation();
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={t("dialogs.move.open", { name: folder.name })}
      accessibilityState={{ disabled: moving }}
      disabled={moving}
      onPress={onOpen}
      className={`min-h-14 flex-row items-center gap-4 px-4 active:bg-surface-container-highest ${moving ? "opacity-40" : ""}`}
    >
      <FileTypeIcon kind="folder" />
      <Text numberOfLines={1} className="flex-1 text-on-surface">
        {folder.name}
      </Text>
      {moving ? null : <Icon name="chevron_right" />}
    </Pressable>
  );
}
