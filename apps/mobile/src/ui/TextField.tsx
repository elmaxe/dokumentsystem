import { useState } from "react";
import { TextInput, View, type TextInputProps } from "react-native";
import { useTheme } from "../state/theme";
import { fontFamilies } from "./Text";
import { Text } from "./Text";

/** An outlined text field with its label above and a hint or error below (docs/design.md §16.5). */
export function TextField({
  label,
  hint,
  error,
  ...input
}: TextInputProps & { label: string; hint?: string; error?: string | null }) {
  const theme = useTheme();
  const [focused, setFocused] = useState(false);
  const border = error ? "border-error border-2" : focused ? "border-primary border-2" : "border-outline border";
  return (
    <View className="gap-1">
      <Text variant="label-lg" className={error ? "text-error" : "text-on-surface-variant"}>
        {label}
      </Text>
      <TextInput
        {...input}
        accessibilityLabel={label}
        accessibilityHint={error ?? hint}
        onFocus={(event) => {
          setFocused(true);
          input.onFocus?.(event);
        }}
        onBlur={(event) => {
          setFocused(false);
          input.onBlur?.(event);
        }}
        placeholderTextColor={theme.color("on-surface-variant")}
        selectionColor={theme.color("primary")}
        style={{ fontFamily: fontFamilies[400], fontSize: 16, color: theme.color("on-surface") }}
        className={`min-h-14 rounded-sm bg-surface-container-lowest px-4 ${border}`}
      />
      {error || hint ? (
        <Text variant="body-sm" accessibilityLiveRegion={error ? "polite" : "none"} className={error ? "text-error" : "text-on-surface-variant"}>
          {error ?? hint}
        </Text>
      ) : null}
    </View>
  );
}
