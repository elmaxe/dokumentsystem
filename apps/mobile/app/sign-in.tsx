import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { ActivityIndicator, ScrollView, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { displayAddress } from "../src/lib/address";
import { sessionQuery } from "../src/state/queries";
import { useSession } from "../src/state/session";
import { findServer, signIn, SignInError } from "../src/state/signIn";
import { useTheme } from "../src/state/theme";
import { Button } from "../src/ui/Button";
import { Icon } from "../src/ui/Icon";
import { Notice } from "../src/ui/Notice";
import { Text } from "../src/ui/Text";

/** Signing in at the server's provider in the system browser (docs/design.md §5). */
export default function SignIn() {
  const { t } = useTranslation();
  const insets = useSafeAreaInsets();
  const theme = useTheme();
  const queryClient = useQueryClient();
  const { state, signedIn, changeServer } = useSession();
  const url = state.status === "signedOut" ? (state.server?.mainUrl ?? "") : "";
  const ended = state.status === "signedOut" && state.ended;
  const found = useQuery({
    queryKey: ["find", url],
    queryFn: () => findServer(url),
    enabled: !!url,
    retry: false,
  });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const provider = found.data?.auth.providerName;

  const start = async () => {
    if (!found.data) return;
    setBusy(true);
    setError(null);
    try {
      const result = await signIn(found.data);
      queryClient.setQueryData(sessionQuery.queryKey, result.session);
      await signedIn({ token: result.token, sessionId: result.session.sessionId });
    } catch (failure) {
      setBusy(false);
      const reason = failure instanceof SignInError ? failure.reason : "failed";
      switch (reason) {
        case "cancelled":
          setError(t("mobile.signIn.cancelled"));
          break;
        case "notConfigured":
          setError(t("mobile.signIn.notConfigured"));
          break;
        case "unreachable":
        case "notDokument":
          setError(t("mobile.signIn.unreachable"));
          break;
        default:
          setError(t(`auth.errors.${reason}`));
      }
    }
  };

  return (
    <ScrollView
      className="flex-1 bg-surface"
      contentContainerStyle={{ paddingTop: insets.top + 48, paddingBottom: insets.bottom + 24 }}
      contentContainerClassName="grow gap-6 px-6"
    >
      <Text variant="headline-sm" accessibilityRole="header" className="text-on-surface">
        {t("mobile.signIn.title")}
      </Text>
      <View className="flex-row items-center gap-3 rounded-md border border-outline-variant bg-surface-container-lowest p-4">
        <Icon name="dns" />
        <View className="min-w-0 flex-1">
          <Text numberOfLines={1} className="text-on-surface">
            {displayAddress(url)}
          </Text>
          {found.data ? (
            <Text variant="body-sm" className="text-on-surface-variant">
              {`${found.data.info.name} ${found.data.info.version}`}
            </Text>
          ) : null}
        </View>
        {found.isPending ? (
          <ActivityIndicator color={theme.color("primary")} />
        ) : found.data ? (
          <View className="flex-row items-center gap-1">
            <Icon name="check_circle" size={18} color="success" filled />
            <Text variant="label-lg" className="text-success">
              {t("mobile.signIn.found")}
            </Text>
          </View>
        ) : (
          <Icon name="error" color="error" filled />
        )}
      </View>
      {ended ? <Notice tone="warning" icon="warning" text={t("live.sessionEnded")} /> : null}
      {found.isError ? (
        <Notice tone="error" icon="error" text={t("mobile.signIn.unreachable")} />
      ) : null}
      {found.data && !found.data.auth.enabled ? (
        <Notice tone="warning" icon="warning" text={t("auth.notConfigured")} />
      ) : null}
      {error ? <Notice tone="error" icon="error" text={error} /> : null}
      <Text variant="body-lg" className="text-on-surface-variant">
        {provider ? t("mobile.signIn.hint", { provider }) : t("mobile.signIn.hintGeneric")}
      </Text>
      <View className="flex-1" />
      <View className="gap-2">
        {found.isError ? (
          <Button
            label={t("mobile.tryAgain")}
            variant="tonal"
            icon="refresh"
            onPress={() => void found.refetch()}
          />
        ) : (
          <Button
            label={provider ? t("auth.signInWith", { provider }) : t("auth.signIn")}
            icon="login"
            onPress={() => void start()}
            busy={busy}
            disabled={!found.data?.auth.enabled}
          />
        )}
        <Button
          label={t("mobile.signIn.otherServer")}
          variant="text"
          onPress={() => void changeServer()}
        />
      </View>
    </ScrollView>
  );
}
