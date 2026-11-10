import { useState } from "react";
import { useTranslation } from "react-i18next";
import { KeyboardAvoidingView, Linking, Platform, ScrollView, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { parseAddress } from "../../../src/lib/address";
import { readWifiName } from "../../../src/lib/wifi";
import { useFormat } from "../../../src/state/format";
import { useSession, useSignedIn } from "../../../src/state/session";
import { Button, IconButton } from "../../../src/ui/Button";
import { Icon } from "../../../src/ui/Icon";
import { Notice } from "../../../src/ui/Notice";
import { Card, Section } from "../../../src/ui/SettingsRows";
import { Text } from "../../../src/ui/Text";
import { TextField } from "../../../src/ui/TextField";
import { TopBar } from "../../../src/ui/TopBar";

/** The main address, an optional local one, and the home Wi-Fi networks it is tried on (docs/design.md §15.3). */
export default function Addresses() {
  const { t } = useTranslation();
  const insets = useSafeAreaInsets();
  const format = useFormat();
  const { setLocalUrl, checkLocal } = useSession();
  const { server, route } = useSignedIn();
  const [draft, setDraft] = useState(server.localUrl ?? "");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const add = async () => {
    // A local address may be plain http: it only ever gets a proof, never the session, until it has proved itself.
    const parsed = parseAddress(draft, { allowHttp: true });
    if (!parsed.ok) {
      setError(t(`mobile.welcome.errors.${parsed.error}`));
      return;
    }
    setBusy(true);
    setError(null);
    await setLocalUrl(parsed.url);
    setBusy(false);
  };

  const test = async () => {
    setBusy(true);
    await checkLocal();
    setBusy(false);
  };

  const checked = route.checkedAt ? format.date(new Date(route.checkedAt), true) : null;
  const status = (() => {
    if (!checked)
      return { text: t("mobile.addresses.notChecked"), className: "text-on-surface-variant" };
    switch (route.local) {
      case "verified":
        return { text: t("mobile.addresses.inUse", { date: checked }), className: "text-success" };
      case "otherNetwork":
        return {
          text: t("mobile.addresses.otherNetwork", { date: checked }),
          className: "text-on-surface-variant",
        };
      case "wrong-server":
        return {
          text: t("mobile.addresses.wrongServer", { date: checked }),
          className: "text-error",
        };
      default:
        return {
          text: t("mobile.addresses.notAnswering", { date: checked }),
          className: "text-on-surface-variant",
        };
    }
  })();

  return (
    <KeyboardAvoidingView
      behavior={Platform.OS === "ios" ? "padding" : undefined}
      className="flex-1 bg-surface"
    >
      <TopBar title={t("mobile.addresses.title")} back />
      <ScrollView
        keyboardShouldPersistTaps="handled"
        contentContainerStyle={{ paddingBottom: insets.bottom + 24 }}
        contentContainerClassName="gap-6 px-4 pt-4"
      >
        <Section title={t("mobile.addresses.main")}>
          <View className="flex-row items-center gap-3 rounded-md bg-surface-container-lowest p-4">
            <Icon name="public" />
            <View className="flex-1">
              <Text className="text-on-surface">{server.mainUrl}</Text>
              <Text variant="body-sm" className="text-on-surface-variant">
                {t("mobile.addresses.mainHint")}
              </Text>
            </View>
          </View>
        </Section>
        <View className="gap-2">
          <TextField
            label={t("mobile.addresses.local")}
            placeholder={t("mobile.addresses.localPlaceholder")}
            value={draft}
            onChangeText={(value) => {
              setDraft(value);
              setError(null);
            }}
            autoCapitalize="none"
            autoCorrect={false}
            keyboardType="url"
            inputMode="url"
            returnKeyType="done"
            onSubmitEditing={() => void add()}
            error={error}
          />
          {server.localUrl ? (
            <View className="flex-row items-center gap-2">
              <Icon name={route.using === "local" ? "wifi" : "wifi_off"} size={20} />
              <Text variant="body-sm" className={`flex-1 ${status.className}`}>
                {status.text}
              </Text>
            </View>
          ) : null}
          <View className="flex-row flex-wrap gap-2">
            {server.localUrl && draft.trim() === server.localUrl ? (
              <>
                <Button
                  label={t("mobile.addresses.test")}
                  variant="tonal"
                  icon="refresh"
                  onPress={() => void test()}
                  busy={busy}
                />
                <Button
                  label={t("mobile.addresses.remove")}
                  variant="text"
                  onPress={() => {
                    setDraft("");
                    void setLocalUrl(null);
                  }}
                />
              </>
            ) : (
              <Button
                label={t("mobile.addresses.add")}
                variant="tonal"
                icon="add"
                onPress={() => void add()}
                busy={busy}
              />
            )}
          </View>
        </View>
        <Notice icon="info" text={t("mobile.addresses.explain")} />
        {server.localUrl ? <HomeNetworks /> : null}
      </ScrollView>
    </KeyboardAvoidingView>
  );
}

/** The Wi-Fi networks the local address is tried on; none tries it everywhere. */
function HomeNetworks() {
  const { t } = useTranslation();
  const { setHomeNetworks } = useSession();
  const { server } = useSignedIn();
  const names = server.homeNetworks ?? [];
  const [problem, setProblem] = useState<"denied" | "notWifi" | "unknown" | null>(null);
  const [busy, setBusy] = useState(false);

  const addThis = async () => {
    setBusy(true);
    setProblem(null);
    const read = await readWifiName().catch(() => ({ error: "unknown" as const }));
    if ("name" in read) {
      if (!names.includes(read.name)) await setHomeNetworks([...names, read.name]);
    } else {
      setProblem(read.error);
    }
    setBusy(false);
  };

  return (
    <Section title={t("mobile.addresses.homeNetworks")}>
      <Text variant="body-sm" className="text-on-surface-variant">
        {t(
          names.length ? "mobile.addresses.homeNetworksOnly" : "mobile.addresses.homeNetworksNone",
        )}
      </Text>
      {names.length ? (
        <Card>
          {names.map((name) => (
            <View key={name} className="min-h-touch flex-row items-center gap-3 pl-4">
              <Icon name="wifi" size={20} />
              <Text className="flex-1 text-on-surface">{name}</Text>
              <IconButton
                icon="close"
                label={t("mobile.addresses.removeNetwork", { name })}
                onPress={() => void setHomeNetworks(names.filter((n) => n !== name))}
              />
            </View>
          ))}
        </Card>
      ) : null}
      {problem ? (
        <Text variant="body-sm" className="text-error">
          {t(`mobile.addresses.wifiProblem.${problem}`)}
        </Text>
      ) : null}
      <View className="flex-row flex-wrap gap-2">
        <Button
          label={t("mobile.addresses.addThisNetwork")}
          variant="tonal"
          icon="add"
          onPress={() => void addThis()}
          busy={busy}
        />
        {problem === "denied" ? (
          <Button
            label={t("mobile.addresses.openSettings")}
            variant="text"
            onPress={() => void Linking.openSettings()}
          />
        ) : null}
      </View>
      <Notice icon="location_on" text={t("mobile.addresses.locationWhy")} />
    </Section>
  );
}
