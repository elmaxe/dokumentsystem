import type { SessionInfo } from "@dokument/api";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { ActivityIndicator, ScrollView, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { useFormat } from "../../../src/state/format";
import { drive, sessionsQuery } from "../../../src/state/queries";
import { useSession } from "../../../src/state/session";
import { useTheme } from "../../../src/state/theme";
import { Button } from "../../../src/ui/Button";
import { Icon } from "../../../src/ui/Icon";
import { Notice } from "../../../src/ui/Notice";
import { Card } from "../../../src/ui/SettingsRows";
import { Text } from "../../../src/ui/Text";
import { TopBar } from "../../../src/ui/TopBar";

/** Where the person is signed in, and a way to sign each one out (docs/design.md §5). */
export default function Devices() {
  const { t } = useTranslation();
  const insets = useSafeAreaInsets();
  const theme = useTheme();
  const format = useFormat();
  const queryClient = useQueryClient();
  const { signOut } = useSession();
  const sessions = useQuery(sessionsQuery);
  const [failure, setFailure] = useState(false);

  const end = async (session: SessionInfo) => {
    if (session.current) {
      await signOut();
      return;
    }
    setFailure(false);
    try {
      await drive.endSession(session.id);
    } catch {
      setFailure(true);
    }
    await queryClient.invalidateQueries({ queryKey: sessionsQuery.queryKey });
  };

  const sorted = [...(sessions.data ?? [])].sort(
    (a, b) => Number(b.current) - Number(a.current) || b.lastSeenAt.localeCompare(a.lastSeenAt),
  );

  return (
    <View className="flex-1 bg-surface">
      <TopBar title={t("mobile.devices.title")} back />
      <ScrollView contentContainerStyle={{ paddingBottom: insets.bottom + 24 }} contentContainerClassName="gap-4 px-4 pt-4">
        <Text variant="body-md" className="text-on-surface-variant">
          {t("mobile.devices.intro")}
        </Text>
        {failure ? <Notice tone="error" icon="error" text={t("mobile.devices.failed")} /> : null}
        {sessions.isPending ? <ActivityIndicator color={theme.color("primary")} /> : null}
        <Card>
          {sorted.map((session, index) => {
            const name = session.deviceName ?? (session.kind === "cookie" ? t("mobile.devices.browser") : t("mobile.devices.unnamed"));
            return (
              <View
                key={session.id}
                className={`flex-row items-center gap-3 py-3 pl-4 pr-2 ${index < sorted.length - 1 ? "border-b border-outline-variant" : ""}`}
              >
                <Icon name={session.kind === "cookie" ? "computer" : "smartphone"} />
                <View className="flex-1">
                  <Text className="text-on-surface">{name}</Text>
                  <Text variant="body-sm" className={session.current ? "text-success" : "text-on-surface-variant"}>
                    {session.current
                      ? t("mobile.devices.thisDevice")
                      : t("mobile.devices.lastUsed", { date: format.date(session.lastSeenAt, true) })}
                  </Text>
                </View>
                <Button
                  label={t("mobile.devices.signOut")}
                  accessibilityLabel={t("mobile.devices.signOutOf", { name })}
                  variant="text"
                  onPress={() => void end(session)}
                />
              </View>
            );
          })}
        </Card>
      </ScrollView>
    </View>
  );
}
