import { loginUrl } from "@dokument/api";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { authConfigQuery } from "./api";
import type { SignInSearch } from "./router";

const knownErrors = ["not_allowed", "disabled", "failed"] as const;

/** Sign-in is delegated to the household's IdP (docs/design.md §5): one button that starts the OIDC login. */
export function SignInPage({ returnTo, error, reason }: SignInSearch) {
  const { t } = useTranslation();
  const config = useQuery(authConfigQuery);
  const errorKey = knownErrors.find((e) => e === error) ?? (error ? "failed" : undefined);
  const provider = config.data?.providerName;

  return (
    <main className="flex min-h-dvh items-center justify-center px-4 py-12 sm:px-6">
      <div className="flex w-full max-w-sm flex-col gap-6 rounded-lg bg-surface-container-low p-6 sm:p-8">
        <div className="flex flex-col gap-4">
          <img src="/favicon.svg" alt="" width={40} height={40} className="size-10" />
          <h1 className="text-headline-sm">{t("auth.title")}</h1>
          <p className="text-body-lg text-on-surface-variant">{t("auth.body")}</p>
        </div>

        {reason === "ended" && !errorKey && (
          <p role="status" className="rounded-sm bg-secondary-container p-3 text-body-md text-on-secondary-container">
            {t("live.sessionEnded")}
          </p>
        )}

        {errorKey && (
          <p
            role="alert"
            className="flex gap-3 rounded-sm bg-error-container p-3 text-body-md text-on-error-container"
          >
            <ErrorIcon />
            <span>{t(`auth.errors.${errorKey}`)}</span>
          </p>
        )}

        {config.data?.enabled === false ? (
          <p role="status" className="text-body-md text-on-surface-variant">
            {t("auth.notConfigured")}
          </p>
        ) : (
          <a
            href={loginUrl(returnTo ?? "/")}
            aria-disabled={!config.data}
            className="inline-flex h-12 items-center justify-center rounded-full bg-primary px-6 text-label-lg text-on-primary transition-shadow duration-100 hover:shadow-elevation-1 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus-ring aria-disabled:pointer-events-none aria-disabled:opacity-60 sm:h-10"
          >
            {provider ? t("auth.signInWith", { provider }) : t("auth.signIn")}
          </a>
        )}
      </div>
    </main>
  );
}

function ErrorIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" className="size-5 shrink-0 fill-current">
      <path d="M12 17q.43 0 .71-.29T13 16t-.29-.71T12 15t-.71.29T11 16t.29.71.71.29m-1-4h2V7h-2zm1 9q-2.07 0-3.9-.79t-3.17-2.14-2.14-3.17T2 12t.79-3.9 2.14-3.17T8.1 2.79 12 2t3.9.79 3.17 2.14 2.14 3.17T22 12t-.79 3.9-2.14 3.17-3.17 2.14T12 22" />
    </svg>
  );
}
