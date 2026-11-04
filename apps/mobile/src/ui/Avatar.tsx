import { View } from "react-native";
import { Text } from "./Text";

/** The person's initial in a circle, for the account button and sheet. */
export function Avatar({ name, size = 32 }: { name: string; size?: number }) {
  const initial = name.trim().charAt(0).toLocaleUpperCase() || "?";
  return (
    <View
      style={{ width: size, height: size }}
      className="items-center justify-center rounded-full bg-primary"
      accessible={false}
      importantForAccessibility="no-hide-descendants"
    >
      <Text variant={size > 40 ? "title-lg" : "label-lg"} className="text-on-primary">
        {initial}
      </Text>
    </View>
  );
}
