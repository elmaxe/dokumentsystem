import { Stack } from "expo-router";
import { useLiveUpdates } from "../../src/state/live";
import { useTheme } from "../../src/state/theme";

/** Everything behind sign-in. Live updates run while it's open (docs/design.md §15.4). */
export default function SignedInLayout() {
  const theme = useTheme();
  useLiveUpdates();
  return <Stack screenOptions={{ headerShown: false, contentStyle: { backgroundColor: theme.color("surface") } }} />;
}
