import { Tabs } from "expo-router";
import { useTranslation } from "react-i18next";
import { Pressable, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { Icon, type IconName } from "../../../src/ui/Icon";
import { OfflineNotice } from "../../../src/ui/OfflineNotice";
import { Text } from "../../../src/ui/Text";

const tabs: { name: string; label: "myFiles" | "sharedWithMe" | "search" | "recent"; icon: IconName }[] = [
  { name: "index", label: "myFiles", icon: "folder" },
  { name: "shared", label: "sharedWithMe", icon: "folder_shared" },
  { name: "search", label: "search", icon: "search" },
  { name: "recent", label: "recent", icon: "schedule" },
];

/** The navigation bar: the four places people go most (docs/design.md §16.3). */
export default function TabsLayout() {
  const { t } = useTranslation();
  const insets = useSafeAreaInsets();
  return (
    <View className="flex-1">
      <Tabs
        screenOptions={{ headerShown: false }}
        tabBar={({ state, navigateToTab }) => (
          <View
            accessibilityRole="tablist"
            style={{ paddingBottom: insets.bottom }}
            className="flex-row bg-surface-container"
          >
            {tabs.map((tab, index) => {
              const selected = state.index === index;
              return (
                <Pressable
                  key={tab.name}
                  accessibilityRole="tab"
                  accessibilityState={{ selected }}
                  onPress={() => {
                    if (!selected) navigateToTab(state.routes[index]!.key);
                  }}
                  className="h-20 flex-1 items-center justify-center gap-1"
                >
                  <View className={`h-8 w-16 items-center justify-center rounded-full ${selected ? "bg-secondary-container" : ""}`}>
                    <Icon
                      name={tab.icon}
                      filled={selected}
                      color={selected ? "on-secondary-container" : "on-surface-variant"}
                    />
                  </View>
                  <Text variant="label-md" numberOfLines={1} className={selected ? "text-on-surface" : "text-on-surface-variant"}>
                    {t(`nav.${tab.label}`)}
                  </Text>
                </Pressable>
              );
            })}
          </View>
        )}
      >
        {tabs.map((tab) => (
          <Tabs.Screen key={tab.name} name={tab.name} options={{ title: t(`nav.${tab.label}`) }} />
        ))}
      </Tabs>
      <OfflineNotice />
    </View>
  );
}
