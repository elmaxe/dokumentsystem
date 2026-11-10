import type { NodeDto } from "@dokument/api";
import { useQuery } from "@tanstack/react-query";
import { router } from "expo-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { ActivityIndicator, View } from "react-native";
import { canEdit } from "../state/changes";
import { folderQuery } from "../state/queries";
import { useTheme } from "../state/theme";
import { EmptyState } from "./EmptyState";
import { FileList, openFile } from "./FileList";
import { ItemSheet } from "./ItemSheet";
import { ListActions } from "./ListActions";
import { Notice } from "./Notice";
import { NewButton } from "./Scan";
import { TopBar } from "./TopBar";
import { SelectionBar, useSelection } from "./Selection";

/** My files, or a folder in it (docs/design.md §16.3). */
export function FolderView({ folderId }: { folderId: string | null }) {
  const { t } = useTranslation();
  const theme = useTheme();
  const listing = useQuery(folderQuery(folderId));
  const [selected, setSelected] = useState<NodeDto | null>(null);
  const selection = useSelection();
  const title = folderId ? (listing.data?.folder?.name ?? "") : t("nav.myFiles");
  const openFolder = (node: NodeDto) => router.push({ pathname: "/folder/[id]", params: { id: node.id } });

  return (
    <View className="flex-1 bg-surface">
      {selection.active ? <SelectionBar selection={selection} /> : <TopBar title={title} back={!!folderId} actions={<ListActions account={!folderId} />} />}
      {listing.isPending ? (
        <View className="flex-1 items-center justify-center">
          <ActivityIndicator color={theme.color("primary")} />
        </View>
      ) : listing.isError && !listing.data ? (
        <View className="p-4">
          <Notice tone="error" icon="error" text={t("errors.generic")} />
        </View>
      ) : (
        <FileList
          nodes={listing.data.items}
          onOpenFolder={openFolder}
          onOpenFile={openFile}
          onMore={setSelected}
          selected={selection.ids}
          onSelect={selection.toggle}
          refreshing={listing.isRefetching}
          onRefresh={() => void listing.refetch()}
          empty={<EmptyState icon="folder_open" title={t("files.empty.folderTitle")} />}
        />
      )}
      {/* Below My files the tab bar is gone, so the button keeps clear of the system's own bar. */}
      {folderId === null || canEdit(listing.data?.folder?.access) ? (
        <NewButton folderId={folderId} folderName={title} safeArea={!!folderId} />
      ) : null}
      <ItemSheet node={selected} onClose={() => setSelected(null)} onOpenFolder={openFolder} onSelect={selection.toggle} />
    </View>
  );
}
