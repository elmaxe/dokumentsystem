import type { Language } from "@dokument/i18n";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { UserProfile } from "@dokument/api";
import { api, unwrap } from "./api";
import { sessionQuery } from "./queries";

/** The person's own settings, saved on the account so they follow them across devices (docs/design.md §16.5, §23). */
export function useAccountSettings() {
  const queryClient = useQueryClient();
  const session = useQuery(sessionQuery);
  const user = session.data?.user;

  const saved = (profile: UserProfile) =>
    queryClient.setQueryData(sessionQuery.queryKey, (current) => (current ? { ...current, user: profile } : current));

  const language = useMutation({
    mutationFn: (choice: "device" | Language) =>
      unwrap(api().PUT("/api/v1/me/language", { body: { language: choice === "device" ? null : choice } })),
    onSuccess: saved,
  });
  const seconds = useMutation({
    mutationFn: (showSeconds: boolean) => unwrap(api().PUT("/api/v1/me/preferences", { body: { showSeconds } })),
    onSuccess: saved,
  });

  return {
    /** Null follows the device. */
    language: (user?.language ?? null) as Language | null,
    showSeconds: user?.showSeconds ?? false,
    setLanguage: language.mutateAsync,
    setShowSeconds: seconds.mutateAsync,
  };
}
