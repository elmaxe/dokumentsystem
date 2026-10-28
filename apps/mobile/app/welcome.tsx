import { useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { KeyboardAvoidingView, Platform, ScrollView, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { parseAddress } from "../src/lib/address";
import { useSession } from "../src/state/session";
import { findServer, SignInError } from "../src/state/signIn";
import { Button } from "../src/ui/Button";
import { Icon } from "../src/ui/Icon";
import { Notice } from "../src/ui/Notice";
import { Text } from "../src/ui/Text";
import { TextField } from "../src/ui/TextField";

/** The first screen: which server to use (docs/design.md §15.3). */
export default function Welcome() {
  const { t } = useTranslation();
  const insets = useSafeAreaInsets();
  const queryClient = useQueryClient();
  const { chooseServer } = useSession();
  const [address, setAddress] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async () => {
    // Plain http is for trying the app against a development server.
    const parsed = parseAddress(address, { allowHttp: __DEV__ });
    if (!parsed.ok) {
      setError(t(`mobile.welcome.errors.${parsed.error}`));
      return;
    }
    setBusy(true);
    setError(null);
    try {
      const found = await findServer(parsed.url);
      queryClient.setQueryData(["find", parsed.url], found);
      await chooseServer(parsed.url);
    } catch (failure) {
      const reason = failure instanceof SignInError && failure.reason === "notDokument" ? "notDokument" : "unreachable";
      setError(t(`mobile.welcome.errors.${reason}`));
      setBusy(false);
    }
  };

  return (
    <KeyboardAvoidingView behavior={Platform.OS === "ios" ? "padding" : undefined} className="flex-1 bg-surface">
      <ScrollView
        keyboardShouldPersistTaps="handled"
        contentContainerStyle={{ paddingTop: insets.top + 48, paddingBottom: insets.bottom + 24 }}
        contentContainerClassName="grow gap-8 px-6"
      >
        <View className="items-center gap-3">
          <View className="h-16 w-16 items-center justify-center rounded-lg bg-primary-container">
            <Icon name="folder_open" size={36} color="on-primary-container" />
          </View>
          <Text variant="title-md" className="text-on-surface-variant">
            {t("app.name")}
          </Text>
        </View>
        <View className="gap-2">
          <Text variant="headline-sm" accessibilityRole="header" className="text-on-surface">
            {t("mobile.welcome.title")}
          </Text>
          <Text variant="body-lg" className="text-on-surface-variant">
            {t("mobile.welcome.body")}
          </Text>
        </View>
        <TextField
          label={t("mobile.welcome.address")}
          placeholder="files.example.com"
          value={address}
          onChangeText={(value) => {
            setAddress(value);
            setError(null);
          }}
          autoCapitalize="none"
          autoCorrect={false}
          autoComplete="url"
          keyboardType="url"
          inputMode="url"
          returnKeyType="go"
          onSubmitEditing={() => void submit()}
          hint={t("mobile.welcome.hint")}
          error={error}
        />
        <Notice icon="router" text={t("mobile.welcome.local")} />
        <View className="flex-1" />
        <Button label={t("mobile.welcome.continue")} onPress={() => void submit()} busy={busy} />
      </ScrollView>
    </KeyboardAvoidingView>
  );
}
