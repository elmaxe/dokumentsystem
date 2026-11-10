import * as Notifications from "expo-notifications";
import { Platform } from "react-native";
import { toast } from "../ui/Snackbar";
import { i18n } from "./i18n";
import { restoreUploads } from "./uploads";

const channel = "uploads";
let ready: Promise<boolean> | null = null;

/**
 * Local notifications only (decision 37): the app has no push. Asked for the first time there's something to say,
 * so the question comes with a reason. Answers whether the app may show them.
 */
function prepare(): Promise<boolean> {
  ready ??= (async () => {
    if (Platform.OS === "web") return false;
    Notifications.setNotificationHandler({
      handleNotification: async () => ({
        shouldShowBanner: true,
        shouldShowList: true,
        shouldPlaySound: false,
        shouldSetBadge: false,
      }),
    });
    if (Platform.OS === "android") {
      await Notifications.setNotificationChannelAsync(channel, {
        name: i18n.t("upload.label"),
        importance: Notifications.AndroidImportance.DEFAULT,
      });
    }
    const current = await Notifications.getPermissionsAsync();
    if (current.granted) return true;
    if (!current.canAskAgain) return false;
    return (await Notifications.requestPermissionsAsync()).granted;
  })().catch(() => false);
  return ready;
}

/**
 * Carries on the uploads the system stopped when the app was closed (docs/design.md §15.3), and says so: a
 * notification, or a snackbar when notifications are off.
 */
export async function resumeUploads() {
  const resumed = await restoreUploads();
  if (!resumed) return;
  const body = resumed.folder
    ? i18n.t("mobile.uploads.resumedIn", { count: resumed.count, folder: resumed.folder })
    : i18n.t("mobile.uploads.resumedBody", { count: resumed.count });
  const allowed = await prepare();
  if (!allowed) {
    toast(i18n.t("mobile.uploads.resumedTitle"));
    return;
  }
  await Notifications.scheduleNotificationAsync({
    content: { title: i18n.t("mobile.uploads.resumedTitle"), body },
    trigger: Platform.OS === "android" ? { channelId: channel } : null,
  }).catch(() => toast(i18n.t("mobile.uploads.resumedTitle")));
}
