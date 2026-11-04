import { pickLanguage, resources, type Language } from "@dokument/i18n";
import { useQuery } from "@tanstack/react-query";
import { getLocales } from "expo-localization";
import i18next from "i18next";
import { useEffect, type ReactNode } from "react";
import { I18nextProvider, initReactI18next } from "react-i18next";
import { sessionQuery } from "./queries";
import { useSession } from "./session";

/** The phone's language, used before signing in and when the account follows the device (docs/design.md §23). */
export const deviceLanguage = (): Language => pickLanguage(getLocales().map((locale) => locale.languageTag));

export const i18n = i18next.createInstance();
void i18n.use(initReactI18next).init({
  lng: deviceLanguage(),
  fallbackLng: "en",
  resources,
  interpolation: { escapeValue: false },
  // The strings are bundled, so there is nothing to wait for.
  initAsync: false,
});

/** Follows the account's language once signed in, like the web app does. */
export function I18nProvider({ children }: { children: ReactNode }) {
  const { state } = useSession();
  const session = useQuery({ ...sessionQuery, enabled: state.status === "signedIn" });
  const language = (state.status === "signedIn" ? session.data?.user.language : null) ?? deviceLanguage();

  useEffect(() => {
    if (i18n.language !== language) void i18n.changeLanguage(language);
  }, [language]);

  return <I18nextProvider i18n={i18n}>{children}</I18nextProvider>;
}
