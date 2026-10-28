import type { ReactNode } from "react";
import { Modal, Pressable, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { useTranslation } from "react-i18next";
import { Text } from "./Text";

/** A bottom sheet, which replaces dialogs and menus on phones (docs/design.md §16.3). */
export function Sheet({
  open,
  onClose,
  title,
  children,
}: {
  open: boolean;
  onClose: () => void;
  title?: string;
  children: ReactNode;
}) {
  const insets = useSafeAreaInsets();
  const { t } = useTranslation();
  return (
    <Modal visible={open} transparent animationType="slide" onRequestClose={onClose} statusBarTranslucent>
      <View className="flex-1 justify-end">
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t("common.close")}
          onPress={onClose}
          className="absolute inset-0 bg-scrim"
        />
        <View
          style={{ paddingBottom: insets.bottom + 16 }}
          className="rounded-t-lg bg-surface-container-high pt-2 shadow-elevation-3"
          accessibilityViewIsModal
        >
          <View className="mb-3 h-1 w-8 self-center rounded-full bg-outline" />
          {title ? (
            <Text variant="title-lg" accessibilityRole="header" className="mx-5 mb-2 text-on-surface">
              {title}
            </Text>
          ) : null}
          {children}
        </View>
      </View>
    </Modal>
  );
}

/** One action in a sheet: an icon and a verb, 48 px tall. */
export function SheetAction({
  icon,
  label,
  onPress,
  danger,
}: {
  icon: ReactNode;
  label: string;
  onPress: () => void;
  danger?: boolean;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      onPress={onPress}
      className="min-h-touch flex-row items-center gap-4 px-5 active:bg-surface-container-highest"
    >
      {icon}
      <Text className={`flex-1 ${danger ? "text-error" : "text-on-surface"}`}>{label}</Text>
    </Pressable>
  );
}
