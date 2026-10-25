import type { UserProfile } from "@dokument/api";
import type { Language } from "@dokument/i18n";
import { useSnackbar } from "@dokument/ui";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, sessionQuery } from "../api";

/** The signed-in person's own settings, saved on the account so they follow them across devices (§16.5, §23). */
export function useAccountSettings() {
  const { t } = useTranslation();
  const toast = useSnackbar();
  const queryClient = useQueryClient();
  const session = useQuery(sessionQuery);
  const user = session.data?.user;

  const saved = (data: UserProfile | undefined, failure: string) => {
    if (!data) {
      toast(failure);
      return;
    }
    queryClient.setQueryData(sessionQuery.queryKey, (current) => (current ? { ...current, user: data } : current));
  };

  return {
    /** Null follows the browser. */
    language: (user?.language ?? null) as Language | null,
    showSeconds: user?.showSeconds ?? false,
    setLanguage: async (choice: "browser" | Language) => {
      const { data } = await api.PUT("/api/v1/me/language", { body: { language: choice === "browser" ? null : choice } });
      saved(data, t("account.languageFailed"));
    },
    setShowSeconds: async (showSeconds: boolean) => {
      const { data } = await api.PUT("/api/v1/me/preferences", { body: { showSeconds } });
      saved(data, t("account.settingFailed"));
    },
  };
}
