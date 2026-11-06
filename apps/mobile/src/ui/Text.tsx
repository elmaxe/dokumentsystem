import { tokens, type TypeStyleToken } from "@dokument/tokens";
import { Platform, Text as NativeText, type TextProps } from "react-native";

type Weight = 400 | 500 | 600;

/** On the web, where expo-font loads each weight at startup as a family of its own. */
export const webFonts = {
  AlbertSans_400Regular: require("@expo-google-fonts/albert-sans/400Regular/AlbertSans_400Regular.ttf"),
  AlbertSans_500Medium: require("@expo-google-fonts/albert-sans/500Medium/AlbertSans_500Medium.ttf"),
  AlbertSans_600SemiBold: require("@expo-google-fonts/albert-sans/600SemiBold/AlbertSans_600SemiBold.ttf"),
  MaterialSymbolsRounded: require("../../assets/fonts/MaterialSymbolsRounded.ttf"),
  MaterialSymbolsRoundedFilled: require("../../assets/fonts/MaterialSymbolsRoundedFilled.ttf"),
};

const webFamilies: Record<Weight, keyof typeof webFonts> = {
  400: "AlbertSans_400Regular",
  500: "AlbertSans_500Medium",
  600: "AlbertSans_600SemiBold",
};

/**
 * Albert Sans in one weight (docs/design.md §16.2). In the app the fonts are built in by expo-font's config plugin
 * (app.config.ts), one family with three weights: Android names it by its XML family, iOS by the font's own name.
 */
export const font = (weight: Weight) =>
  Platform.select({
    ios: { fontFamily: "Albert Sans", fontWeight: String(weight) as "400" | "500" | "600" },
    android: { fontFamily: "AlbertSans", fontWeight: String(weight) as "400" | "500" | "600" },
    default: { fontFamily: webFamilies[weight] },
  });

const px = (value: string) => Number.parseFloat(value);

const styles = Object.fromEntries(
  Object.entries(tokens.typography).map(([name, style]) => [
    name,
    {
      fontSize: px(style.fontSize),
      lineHeight: px(style.lineHeight),
      ...(name === "mono-sm" ? {} : font(Number(style.fontWeight) as Weight)),
      ...(style.letterSpacing ? { letterSpacing: px(style.letterSpacing) } : {}),
    },
  ]),
) as Record<
  TypeStyleToken,
  {
    fontSize: number;
    lineHeight: number;
    fontFamily?: string;
    fontWeight?: "400" | "500" | "600";
    letterSpacing?: number;
  }
>;

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
