import { tokens, type TypeStyleToken } from "@dokument/tokens";
import { Text as NativeText, type TextProps } from "react-native";

/** Albert Sans as bundled by expo-font: React Native needs one family per weight (docs/design.md §16.2). */
export const fontFamilies = {
  400: "AlbertSans_400Regular",
  500: "AlbertSans_500Medium",
  600: "AlbertSans_600SemiBold",
} as const;

const px = (value: string) => Number.parseFloat(value);

const styles = Object.fromEntries(
  Object.entries(tokens.typography).map(([name, style]) => [
    name,
    {
      fontSize: px(style.fontSize),
      lineHeight: px(style.lineHeight),
      fontFamily: name === "mono-sm" ? undefined : fontFamilies[style.fontWeight as keyof typeof fontFamilies],
      ...(style.letterSpacing ? { letterSpacing: px(style.letterSpacing) } : {}),
    },
  ]),
) as Record<TypeStyleToken, { fontSize: number; lineHeight: number; fontFamily?: string; letterSpacing?: number }>;

/** Text in one of the type styles. Colour comes from a class, such as `text-on-surface-variant`. */
export function Text({
  variant = "body-lg",
  className,
  style,
  ...props
}: TextProps & { variant?: TypeStyleToken; className?: string }) {
  return (
    <NativeText
      {...props}
      className={className ?? "text-on-surface"}
      style={[styles[variant], { fontVariant: ["tabular-nums"] }, style]}
    />
  );
}
