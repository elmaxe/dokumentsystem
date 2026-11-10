import { Stack } from "expo-router";
import { View } from "react-native";
import { useLiveUpdates } from "../../src/state/live";
import { useTheme } from "../../src/state/theme";
import { SnackbarHost } from "../../src/ui/Snackbar";

/** Everything behind sign-in. Live updates run while it's open (docs/design.md §15.4). */
export default function SignedInLayout() {
  const theme = useTheme();
  useLiveUpdates();
  return (
    <View className="flex-1">
      <Stack
        screenOptions={{
          headerShown: false,
          contentStyle: { backgroundColor: theme.color("surface") },
        }}
      />
      <SnackbarHost />
    </View>
  );
}
