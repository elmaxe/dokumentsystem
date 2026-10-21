import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { serverInfoQuery } from "./api";

/**
 * Placeholder start page that proves the tokens, i18n and API client are wired up.
 * The real file browser waits for the clickable mockup (docs/design.md §16.5).
 */
export function HomePage() {
  const { t } = useTranslation();
  const info = useQuery(serverInfoQuery);

  return (
    <main className="mx-auto flex min-h-dvh max-w-xl flex-col justify-center gap-4 px-4 sm:px-6">
      <h1 className="text-headline-sm sm:text-headline-md">{t("app.name")}</h1>
      <p className="text-body-lg text-on-surface-variant" role="status">
        {info.isPending && t("status.connecting")}
        {info.isError && <span className="text-error">{t("status.unreachable")}</span>}
        {info.isSuccess && t("status.connected", { version: info.data.version })}
      </p>
    </main>
  );
}
