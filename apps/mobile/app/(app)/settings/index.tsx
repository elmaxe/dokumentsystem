import type { Language } from "@dokument/i18n";
import { useQuery } from "@tanstack/react-query";
import * as Application from "expo-application";
import { router } from "expo-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { ScrollView, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { displayAddress } from "../../../src/lib/address";
import { useAccountSettings } from "../../../src/state/account";
import { useFormat } from "../../../src/state/format";
import { serverInfoQuery, sessionsQuery } from "../../../src/state/queries";
import { useSignedIn } from "../../../src/state/session";
import { useTheme, type ThemePreference } from "../../../src/state/theme";
import { Icon } from "../../../src/ui/Icon";
import { Notice } from "../../../src/ui/Notice";
import { Card, NavRow, Section, Segmented, SwitchRow } from "../../../src/ui/SettingsRows";
import { Sheet, SheetAction } from "../../../src/ui/Sheet";
import { Text } from "../../../src/ui/Text";
import { TopBar } from "../../../src/ui/TopBar";

const languageNames: Record<Language, string> = { sv: "Svenska", en: "English" };

/** The app's settings: the account's own, the phone's appearance, and the connection (docs/design.md §16.5). */
export default function Settings() {
  const { t } = useTranslation();
  const insets = useSafeAreaInsets();
  const theme = useTheme();
  const format = useFormat();
  const account = useAccountSettings();
  const { server, route } = useSignedIn();
  const info = useQuery(serverInfoQuery);
  const sessions = useQuery(sessionsQuery);
  const [choosingLanguage, setChoosingLanguage] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);

  const save = async (change: Promise<unknown>, message: string) => {
    setFailure(null);
    try {
      await change;
    } catch {
      setFailure(message);
    }
  };

  const themes: { value: ThemePreference; label: string }[] = [
    { value: "system", label: t("settings.appearance.system") },
    { value: "light", label: t("settings.appearance.light") },
    { value: "dark", label: t("settings.appearance.dark") },
  ];

  return (
    <View className="flex-1 bg-surface">
      <TopBar title={t("settings.title")} back />
      <ScrollView
        contentContainerStyle={{ paddingBottom: insets.bottom + 24 }}
        contentContainerClassName="gap-6 px-4 pt-4"
      >
        {failure ? <Notice tone="error" icon="error" text={failure} /> : null}
        <Section title={t("account.language")}>
          <Card>
            <NavRow
              icon="translate"
              label={
                account.language
                  ? languageNames[account.language]
                  : t("mobile.settings.languageDevice")
              }
              onPress={() => setChoosingLanguage(true)}
            />
          </Card>
        </Section>
        <Section title={t("settings.appearance.title")}>
          <Segmented
            label={t("settings.appearance.title")}
            options={themes}
            value={theme.preference}
            onChange={theme.setPreference}
          />
          <Text variant="body-sm" className="text-on-surface-variant">
            {`${theme.scheme === "dark" ? t("settings.appearance.nowDark") : t("settings.appearance.nowLight")} · ${t("mobile.settings.appearanceNote")}`}
          </Text>
        </Section>
        <Section title={t("settings.time.title")}>
          <SwitchRow
            label={t("settings.time.seconds")}
            hint={t("settings.time.example", { example: format.date(new Date()) })}
            value={account.showSeconds}
            onChange={(value) =>
              void save(account.setShowSeconds(value), t("account.settingFailed"))
            }
          />
        </Section>
        <Section title={t("mobile.settings.connection")}>
          <Card>
            <NavRow
              icon="router"
              label={t("mobile.settings.addresses")}
              value={
                route.using === "local"
                  ? t("mobile.settings.usingLocal")
                  : t("mobile.settings.usingMain")
              }
              valueClassName={route.using === "local" ? "text-success" : undefined}
              onPress={() => router.push("/settings/addresses")}
              divider
            />
            <NavRow
              icon="devices"
              label={t("mobile.settings.devices")}
              value={
                sessions.data
                  ? t("mobile.settings.devicesCount", { count: sessions.data.length })
                  : undefined
              }
              onPress={() => router.push("/settings/devices")}
            />
          </Card>
        </Section>
        <Section title={t("mobile.settings.about")}>
          <Text variant="body-md" className="text-on-surface-variant">
            {t("mobile.settings.versions", {
              app: Application.nativeApplicationVersion ?? "dev",
              server: info.data?.version ?? "…",
              host: displayAddress(server.mainUrl),
            })}
          </Text>
        </Section>
      </ScrollView>
      <Sheet
        open={choosingLanguage}
        onClose={() => setChoosingLanguage(false)}
        title={t("account.language")}
      >
        {(["device", "sv", "en"] as const).map((choice) => {
          const selected = (account.language ?? "device") === choice;
          return (
            <SheetAction
              key={choice}
              icon={
                <Icon
                  name={selected ? "check" : "radio_button_unchecked"}
                  color={selected ? "primary" : "outline"}
                />
              }
              label={
                choice === "device" ? t("mobile.settings.languageDevice") : languageNames[choice]
              }
              selected={selected}
              onPress={() => {
                setChoosingLanguage(false);
                void save(account.setLanguage(choice), t("account.languageFailed"));
              }}
            />
          );
        })}
      </Sheet>
    </View>
  );
}
