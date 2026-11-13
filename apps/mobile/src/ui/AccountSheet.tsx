import { useQuery } from "@tanstack/react-query";
import { router } from "expo-router";
import * as WebBrowser from "expo-web-browser";
import { useTranslation } from "react-i18next";
import { View } from "react-native";
import { sessionQuery, storageQuery } from "../state/queries";
import { useSession, useSignedIn } from "../state/session";
import { Avatar } from "./Avatar";
import { Icon } from "./Icon";
import { Sheet, SheetAction } from "./Sheet";
import { StorageMeter } from "./StorageMeter";
import { Text } from "./Text";

/** Who's signed in, their storage and the places that aren't tabs (docs/design.md §16.3). */
export function AccountSheet({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const { signOut } = useSession();
  const { server } = useSignedIn();
  const session = useQuery(sessionQuery);
  const storage = useQuery({ ...storageQuery, enabled: open });
  const user = session.data?.user;

  const go = (href: "/starred" | "/trash" | "/settings") => {
    onClose();
    router.push(href);
  };

  return (
    <Sheet open={open} onClose={onClose}>
      {user ? (
        <View className="mx-5 mb-4 flex-row items-center gap-3">
          <Avatar name={user.displayName} id={user.id} avatar={user.avatar} size={56} tone={1} />
          <View className="min-w-0 flex-1">
            <Text variant="title-md" numberOfLines={1} className="text-on-surface">
              {user.displayName}
            </Text>
            {user.email ? (
              <Text variant="body-sm" numberOfLines={1} className="text-on-surface-variant">
                {user.email}
              </Text>
            ) : null}
          </View>
        </View>
      ) : null}
      {storage.data ? (
        <View className="mx-5 mb-3 rounded-md bg-surface-container-low p-4">
          <StorageMeter usage={storage.data} />
        </View>
      ) : null}
      <SheetAction icon={<Icon name="star" />} label={t("nav.starred")} onPress={() => go("/starred")} />
      <SheetAction icon={<Icon name="delete" />} label={t("nav.trash")} onPress={() => go("/trash")} />
      <SheetAction icon={<Icon name="settings" />} label={t("account.settings")} onPress={() => go("/settings")} />
      {user?.role === "admin" ? (
        // Admin pages stay in the browser (decision 6).
        <SheetAction
          icon={<Icon name="open_in_new" />}
          label={t("mobile.account.openAdmin")}
          onPress={() => {
            onClose();
            void WebBrowser.openBrowserAsync(`${server.mainUrl}/admin`);
          }}
        />
      ) : null}
      <SheetAction
        icon={<Icon name="logout" />}
        label={t("auth.signOut")}
        onPress={() => {
          onClose();
          void signOut();
        }}
      />
    </Sheet>
  );
}
