import type { ColorToken } from "@dokument/tokens";
import { View } from "react-native";
import { Icon, type IconName } from "./Icon";
import { Text } from "./Text";

type Tone = "info" | "success" | "warning" | "error";

const tones: Record<Tone, { box: string; text: string; icon: ColorToken }> = {
  info: { box: "bg-surface-container-low", text: "text-on-surface-variant", icon: "on-surface-variant" },
  success: { box: "bg-success-container", text: "text-on-surface", icon: "success" },
  warning: { box: "bg-warning-container", text: "text-on-surface", icon: "warning" },
  error: { box: "bg-error-container", text: "text-on-error-container", icon: "error" },
};

/** State in words, with an icon and a colour (docs/design.md §16.1). */
export function Notice({ icon, text, tone = "info" }: { icon: IconName; text: string; tone?: Tone }) {
  const look = tones[tone];
  return (
    <View accessibilityRole={tone === "error" ? "alert" : undefined} className={`flex-row items-start gap-3 rounded-md p-3 ${look.box}`}>
      <Icon name={icon} size={20} color={look.icon} filled={tone !== "info"} />
      <Text variant="body-md" className={`flex-1 ${look.text}`}>
        {text}
      </Text>
    </View>
  );
}
