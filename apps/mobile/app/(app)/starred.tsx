import type { NodeDto } from "@dokument/api";
import { useQuery } from "@tanstack/react-query";
import { router } from "expo-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { View } from "react-native";
import { starredQuery } from "../../src/state/queries";
import { EmptyState } from "../../src/ui/EmptyState";
import { FileList } from "../../src/ui/FileList";
import { ItemSheet } from "../../src/ui/ItemSheet";
import { ListActions } from "../../src/ui/ListActions";
import { TopBar } from "../../src/ui/TopBar";

/** The person's starred files and folders, wherever they are (docs/design.md §13.3). */
export default function Starred() {
  const { t } = useTranslation();
  const starred = useQuery(starredQuery);
  const [selected, setSelected] = useState<NodeDto | null>(null);
  const openFolder = (node: NodeDto) =>
    router.push({ pathname: "/folder/[id]", params: { id: node.id } });

  return (
    <View className="flex-1 bg-surface">
      <TopBar title={t("nav.starred")} back actions={<ListActions />} />
      <FileList
        nodes={starred.data?.items.map((item) => item.node) ?? []}
        onOpenFolder={openFolder}
        onOpenFile={setSelected}
        onMore={setSelected}
        refreshing={starred.isRefetching}
        onRefresh={() => void starred.refetch()}
        empty={
          starred.isPending ? undefined : (
            <EmptyState
              icon="star"
              title={t("files.empty.starredTitle")}
              text={t("files.empty.starredText")}
            />
          )
        }
      />
      <ItemSheet node={selected} onClose={() => setSelected(null)} onOpenFolder={openFolder} />
    </View>
  );
}
