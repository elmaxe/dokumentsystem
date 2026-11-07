import type { NodeDto } from "@dokument/api";
import { router } from "expo-router";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { BackHandler, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { readablePdf } from "../state/pdf";
import { IconButton } from "./Button";
import { Text } from "./Text";

/**
 * Files picked with a long press (docs/design.md §8.4), in the order they were picked, which is the order a merge
 * starts from. The system's back gesture clears them before it leaves the page.
 */
export function useSelection() {
  const [nodes, setNodes] = useState<NodeDto[]>([]);
  const ids = new Set(nodes.map((node) => node.id));
  const clear = () => setNodes([]);

  useEffect(() => {
    if (nodes.length === 0) return;
    const subscription = BackHandler.addEventListener("hardwareBackPress", () => {
      setNodes([]);
      return true;
    });
    return () => subscription.remove();
  }, [nodes.length]);

  return {
    nodes,
    ids,
    active: nodes.length > 0,
    toggle: (node: NodeDto) =>
      setNodes((list) => (list.some((other) => other.id === node.id) ? list.filter((other) => other.id !== node.id) : [...list, node])),
    clear,
  };
}

export type Selection = ReturnType<typeof useSelection>;

/** Stands in for the top bar while files are selected: how many, and what can be done with them. */
export function SelectionBar({ selection }: { selection: Selection }) {
  const { t } = useTranslation();
  const insets = useSafeAreaInsets();
  const { nodes } = selection;
  const pdfs = nodes.every(readablePdf);
  const single = nodes.length === 1 ? nodes[0] : undefined;
  return (
    <View style={{ paddingTop: insets.top }} className="bg-secondary-container">
      <View className="h-topbar flex-row items-center gap-1 pl-1 pr-2" accessibilityLabel={t("selection.label")}>
        <IconButton icon="close" label={t("selection.clear")} onPress={selection.clear} color="on-secondary-container" />
        <Text
          variant="title-lg"
          numberOfLines={1}
          accessibilityRole="header"
          accessibilityLiveRegion="polite"
          className="flex-1 text-on-secondary-container"
        >
          {t("selection.count", { count: nodes.length })}
        </Text>
        {single && pdfs ? (
          <IconButton
            icon="call_split"
            label={t("pdf.split.action")}
            color="on-secondary-container"
            onPress={() => {
              selection.clear();
              router.push({ pathname: "/split/[id]", params: { id: single.id } });
            }}
          />
        ) : null}
        {nodes.length > 1 && pdfs ? (
          <IconButton
            icon="call_merge"
            label={t("pdf.merge.action")}
            color="on-secondary-container"
            onPress={() => {
              selection.clear();
              router.push({ pathname: "/merge", params: { ids: nodes.map((node) => node.id).join(",") } });
            }}
          />
        ) : null}
      </View>
    </View>
  );
}
