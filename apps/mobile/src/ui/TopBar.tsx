import { router } from "expo-router";
import type { ReactNode } from "react";
import { View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { useTranslation } from "react-i18next";
import { IconButton } from "./Button";
import { Text } from "./Text";

/**
 * The top bar: a page title on top-level places, or a back arrow and a title on the pages under them
 * (docs/design.md §16.3).
 */
export function TopBar({ title, back, actions }: { title: string; back?: boolean; actions?: ReactNode }) {
  const insets = useSafeAreaInsets();
  const { t } = useTranslation();
  return (
    <View style={{ paddingTop: insets.top }} className="bg-surface-container-low">
      <View className={`h-topbar flex-row items-center gap-1 ${back ? "pl-1" : "pl-4"} pr-2`}>
        {back ? <IconButton icon="arrow_back" label={t("common.back")} onPress={() => router.back()} color="on-surface" /> : null}
        <Text
          variant={back ? "title-lg" : "headline-sm"}
          numberOfLines={1}
          accessibilityRole="header"
          className="flex-1 text-on-surface"
        >
          {title}
        </Text>
        {actions}
      </View>
    </View>
  );
}
