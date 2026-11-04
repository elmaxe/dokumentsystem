import type { ReactNode } from "react";
import { View } from "react-native";
import { Icon, type IconName } from "./Icon";
import { Text } from "./Text";

export function EmptyState({ icon, title, text, children }: { icon: IconName; title: string; text?: string; children?: ReactNode }) {
  return (
    <View className="flex-1 items-center justify-center gap-3 px-8 py-16">
      <Icon name={icon} size={48} />
      <Text variant="title-md" className="text-center text-on-surface">
        {title}
      </Text>
      {text ? (
        <Text variant="body-md" className="text-center text-on-surface-variant">
          {text}
        </Text>
      ) : null}
      {children}
    </View>
  );
}
