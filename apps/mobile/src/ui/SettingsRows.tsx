import type { ReactNode } from "react";
import { Pressable, Switch, View } from "react-native";
import { useTheme } from "../state/theme";
import { Icon, type IconName } from "./Icon";
import { Text } from "./Text";

/** A titled group of settings. */
export function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <View className="gap-2">
      <Text variant="title-sm" accessibilityRole="header" className="text-primary">
        {title}
      </Text>
      {children}
    </View>
  );
}

/** Rows on one card, with dividers between them. */
export function Card({ children }: { children: ReactNode }) {
  return <View className="overflow-hidden rounded-md bg-surface-container-lowest">{children}</View>;
}

/** A row that opens another page or a sheet. */
export function NavRow({
  icon,
  label,
  value,
  valueClassName,
  onPress,
  divider,
}: {
  icon: IconName;
  label: string;
  value?: string;
  valueClassName?: string;
  onPress: () => void;
  divider?: boolean;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityHint={value}
      onPress={onPress}
      className={`min-h-16 flex-row items-center gap-3 py-2 pl-4 pr-3 active:bg-surface-container-high ${divider ? "border-b border-outline-variant" : ""}`}
    >
      <Icon name={icon} />
      <View className="flex-1">
        <Text className="text-on-surface">{label}</Text>
        {value ? (
          <Text variant="body-sm" className={valueClassName ?? "text-on-surface-variant"}>
            {value}
          </Text>
        ) : null}
      </View>
      <Icon name="chevron_right" />
    </Pressable>
  );
}

export function SwitchRow({
  label,
  hint,
  value,
  onChange,
}: {
  label: string;
  hint?: string;
  value: boolean;
  onChange: (value: boolean) => void;
}) {
  const theme = useTheme();
  return (
    <View className="min-h-16 flex-row items-center gap-3 rounded-md bg-surface-container-lowest px-4 py-2">
      <View className="flex-1">
        <Text className="text-on-surface">{label}</Text>
        {hint ? (
          <Text variant="body-sm" className="text-on-surface-variant">
            {hint}
          </Text>
        ) : null}
      </View>
      <Switch
        accessibilityLabel={label}
        value={value}
        onValueChange={onChange}
        trackColor={{ true: theme.color("primary"), false: theme.color("surface-container-highest") }}
        thumbColor={theme.color(value ? "on-primary" : "outline")}
      />
    </View>
  );
}

/** A segmented button: one choice of a few, all visible (docs/design.md §16.5). */
export function Segmented<T extends string>({
  label,
  options,
  value,
  onChange,
}: {
  label: string;
  options: { value: T; label: string }[];
  value: T;
  onChange: (value: T) => void;
}) {
  return (
    <View accessibilityRole="radiogroup" accessibilityLabel={label} className="flex-row overflow-hidden rounded-full border border-outline">
      {options.map((option, index) => {
        const selected = option.value === value;
        return (
          <Pressable
            key={option.value}
            accessibilityRole="radio"
            accessibilityState={{ checked: selected }}
            onPress={() => onChange(option.value)}
            className={`min-h-10 flex-1 flex-row items-center justify-center gap-1 px-1.5 py-1 ${selected ? "bg-secondary-container" : ""} ${index < options.length - 1 ? "border-r border-outline" : ""}`}
          >
            {selected ? <Icon name="check" size={16} color="on-secondary-container" /> : null}
            <Text
              variant="label-lg"
              numberOfLines={2}
              className={`text-center ${selected ? "text-on-secondary-container" : "text-on-surface"}`}
            >
              {option.label}
            </Text>
          </Pressable>
        );
      })}
    </View>
  );
}
