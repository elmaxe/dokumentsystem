import { useNetInfo } from "@react-native-community/netinfo";
import { useTranslation } from "react-i18next";
import { View } from "react-native";
import { Icon } from "./Icon";
import { Text } from "./Text";

/**
 * Says the phone is offline and that what's shown is from last time (decision 5 in docs/design.md §15.3). Sits above
 * the navigation bar, in the inverse colours snackbars use.
 */
export function OfflineNotice() {
  const { t } = useTranslation();
  const net = useNetInfo();
  if (net.isConnected !== false) return null;
  return (
    <View
      accessibilityRole="alert"
      className="absolute bottom-24 left-3 right-3 flex-row items-start gap-3 rounded-md bg-on-surface px-4 py-3.5 shadow-elevation-2"
    >
      <Icon name="cloud_off" color="surface-container-highest" />
      <View className="flex-1 gap-0.5">
        <Text variant="title-sm" className="text-surface">
          {t("mobile.offline.title")}
        </Text>
        <Text variant="body-md" className="text-surface-container-highest">
          {t("mobile.offline.text")}
        </Text>
      </View>
    </View>
  );
}
