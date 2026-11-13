import { router, Stack, usePathname } from "expo-router";
import { useShareIntentContext } from "expo-share-intent";
import { useEffect } from "react";
import { View } from "react-native";
import { useLiveUpdates } from "../../src/state/live";
import { resumeUploads } from "../../src/state/notify";
import { useSharedWithYouNotices } from "../../src/state/sharing";
import { useTheme } from "../../src/state/theme";
import { SnackbarHost } from "../../src/ui/Snackbar";
import { UploadPrompts } from "../../src/ui/UploadPrompts";

/**
 * Everything behind sign-in. Live updates run while it's open (docs/design.md §15.4), uploads left over from the last
 * time carry on, files shared from another app open the share screen (§15.3), and a snackbar says when someone
 * shares something with the person (§9).
 */
export default function SignedInLayout() {
  const theme = useTheme();
  const pathname = usePathname();
  const { hasShareIntent } = useShareIntentContext();
  useLiveUpdates();
  useSharedWithYouNotices();

  useEffect(() => {
    void resumeUploads();
  }, []);

  useEffect(() => {
    if (hasShareIntent && pathname !== "/share") router.push("/share");
  }, [hasShareIntent, pathname]);

  return (
    <View className="flex-1">
      <Stack
        screenOptions={{
          headerShown: false,
          contentStyle: { backgroundColor: theme.color("surface") },
        }}
      />
      <UploadPrompts />
      <SnackbarHost />
    </View>
  );
}
