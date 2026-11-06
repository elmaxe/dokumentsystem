import type { ColorToken } from "@dokument/tokens";
import { Text } from "react-native";
import { useTheme } from "../state/theme";
import { iconCodepoints } from "./iconCodepoints";

export type IconName = keyof typeof iconCodepoints;

/**
 * A Material Symbols Rounded icon, outlined or filled (docs/design.md §16.5). Icons are decoration: the control
 * around one carries its label.
 */
export function Icon({
  name,
  size = 24,
  color = "on-surface-variant",
  filled = false,
}: {
  name: IconName;
  size?: number;
  color?: ColorToken;
  filled?: boolean;
}) {
  const theme = useTheme();
  return (
    <Text
      accessible={false}
      importantForAccessibility="no"
      allowFontScaling={false}
      style={{
        fontFamily: filled ? "MaterialSymbolsRoundedFilled" : "MaterialSymbolsRounded",
        fontSize: size,
        lineHeight: size,
        width: size,
        height: size,
        textAlign: "center",
        color: theme.color(color),
      }}
    >
      {String.fromCodePoint(iconCodepoints[name])}
    </Text>
  );
}
