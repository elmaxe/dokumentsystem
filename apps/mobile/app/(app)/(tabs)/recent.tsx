import type { NodeDto } from "@dokument/api";
import { useQuery } from "@tanstack/react-query";
import { router } from "expo-router";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { View } from "react-native";
import { useFormat } from "../../../src/state/format";
import { recentQuery } from "../../../src/state/queries";
import { EmptyState } from "../../../src/ui/EmptyState";
import { FileList, openFile } from "../../../src/ui/FileList";
import { ItemSheet } from "../../../src/ui/ItemSheet";
import { AccountButton } from "../../../src/ui/ListActions";
import { TopBar } from "../../../src/ui/TopBar";
import { SelectionBar, useSelection } from "../../../src/ui/Selection";

/** What the person opened, downloaded or uploaded lately, newest first (docs/design.md §13.3). */
export default function Recent() {
  const { t } = useTranslation();
  const format = useFormat();
  const recent = useQuery(recentQuery);
  const [selected, setSelected] = useState<NodeDto | null>(null);
  const selection = useSelection();
  const items = useMemo(() => recent.data?.items ?? [], [recent.data]);
  const activity = useMemo(() => new Map(items.map((item) => [item.node.id, item])), [items]);
  const openFolder = (node: NodeDto) =>
    router.push({ pathname: "/folder/[id]", params: { id: node.id } });

  return (
    <View className="flex-1 bg-surface">
      {selection.active ? (
        <SelectionBar selection={selection} />
      ) : (
        <TopBar title={t("nav.recent")} actions={<AccountButton />} />
      )}
      <FileList
        nodes={items.map((item) => item.node)}
        keepOrder
        view="list"
        meta={(node) => {
          const item = activity.get(node.id);
          return item
            ? t(`files.activity.${item.action}`, { date: format.date(item.at, true) })
            : format.date(node.modifiedAt);
        }}
        onOpenFolder={openFolder}
        onOpenFile={openFile}
        onMore={setSelected}
        selected={selection.ids}
        onSelect={selection.toggle}
        refreshing={recent.isRefetching}
        onRefresh={() => void recent.refetch()}
        empty={
          recent.isPending ? undefined : (
            <EmptyState
              icon="schedule"
              title={t("files.empty.recentTitle")}
              text={t("files.empty.recentText")}
            />
          )
        }
      />
      <ItemSheet
        node={selected}
        onClose={() => setSelected(null)}
        onOpenFolder={openFolder}
        onSelect={selection.toggle}
      />
    </View>
  );
}
