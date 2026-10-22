import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { logout, serverInfoQuery, sessionQuery } from "./api";

/**
 * Placeholder start page that proves the tokens, i18n, API client and sign-in are wired up.
 * The real file browser waits for the clickable mockup (docs/design.md §16.5).
 */
export function HomePage() {
  const { t } = useTranslation();
  const info = useQuery(serverInfoQuery);
  const session = useQuery(sessionQuery);
  const queryClient = useQueryClient();
  const navigate = useNavigate();

  async function signOut() {
    await logout();
    queryClient.setQueryData(sessionQuery.queryKey, null);
    await navigate({ to: "/signin" });
  }

  return (
    <main className="mx-auto flex min-h-dvh max-w-xl flex-col justify-center gap-4 px-4 sm:px-6">
      <h1 className="text-headline-sm sm:text-headline-md">{t("app.name")}</h1>
      <p className="text-body-lg text-on-surface-variant" role="status">
        {info.isPending && t("status.connecting")}
        {info.isError && <span className="text-error">{t("status.unreachable")}</span>}
        {info.isSuccess && t("status.connected", { version: info.data.version })}
      </p>
      {session.data && (
        <div className="flex flex-wrap items-center gap-4">
          <p className="text-body-lg">{t("auth.signedInAs", { name: session.data.user.displayName })}</p>
          <button
            type="button"
            onClick={() => void signOut()}
            className="inline-flex h-12 items-center rounded-full border border-outline px-6 text-label-lg text-primary focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus-ring sm:h-10"
          >
            {t("auth.signOut")}
          </button>
        </div>
      )}
    </main>
  );
}
