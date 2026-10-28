import type { ColorToken } from "@dokument/tokens";
import { ActivityIndicator, Pressable, View } from "react-native";
import { useTheme } from "../state/theme";
import { Icon, type IconName } from "./Icon";
import { Text } from "./Text";

type Variant = "filled" | "tonal" | "outlined" | "text" | "danger";

const look: Record<Variant, { box: string; text: string; icon: ColorToken }> = {
  filled: { box: "bg-primary", text: "text-on-primary", icon: "on-primary" },
  tonal: { box: "bg-secondary-container", text: "text-on-secondary-container", icon: "on-secondary-container" },
  outlined: { box: "border border-outline", text: "text-primary", icon: "primary" },
  text: { box: "", text: "text-primary", icon: "primary" },
  danger: { box: "", text: "text-error", icon: "error" },
};

/** Buttons are verbs (docs/design.md §16.5). At most one filled button per screen. */
export function Button({
  label,
  onPress,
  variant = "filled",
  icon,
  disabled,
  busy,
  className,
  accessibilityLabel,
}: {
  label: string;
  /** When the visible label needs more to make sense on its own, such as which device "Sign out" is for. */
  accessibilityLabel?: string;
  onPress?: () => void;
  variant?: Variant;
  icon?: IconName;
  disabled?: boolean;
  busy?: boolean;
  className?: string;
}) {
  const theme = useTheme();
  const style = look[variant];
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={accessibilityLabel}
      accessibilityState={{ disabled: !!disabled || !!busy, busy: !!busy }}
      disabled={disabled || busy}
      onPress={onPress}
      className={`min-h-touch flex-row items-center justify-center gap-2 rounded-full px-6 active:opacity-80 ${style.box} ${disabled ? "opacity-40" : ""} ${className ?? ""}`}
    >
      {busy ? (
        <ActivityIndicator color={theme.color(style.icon)} />
      ) : icon ? (
        <Icon name={icon} size={20} color={style.icon} />
      ) : null}
      <Text variant="label-lg" className={style.text}>
        {label}
      </Text>
    </Pressable>
  );
}

export function IconButton({
  icon,
  label,
  onPress,
  filled,
  color = "on-surface-variant",
  disabled,
}: {
  icon: IconName;
  label: string;
  onPress?: () => void;
  filled?: boolean;
  color?: ColorToken;
  disabled?: boolean;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={label}
      disabled={disabled}
      onPress={onPress}
      hitSlop={4}
      className={`h-touch w-touch items-center justify-center rounded-full active:bg-surface-container-highest ${disabled ? "opacity-40" : ""}`}
    >
      <View>
        <Icon name={icon} filled={filled} color={color} />
      </View>
    </Pressable>
  );
}
