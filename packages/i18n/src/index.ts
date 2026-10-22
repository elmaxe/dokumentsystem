import i18next, { type i18n } from "i18next";
import { en } from "./en";
import { sv } from "./sv";

export type Strings = typeof en;
export const languages = ["sv", "en"] as const;
export type Language = (typeof languages)[number];
export const resources = { en: { translation: en }, sv: { translation: sv } } as const;

/** Picks Swedish for any sv-* locale and English otherwise. */
export function pickLanguage(preferred: readonly string[]): Language {
  for (const tag of preferred) {
    const base = tag.toLowerCase().split("-")[0];
    if (base === "sv" || base === "en") return base;
  }
  return "en";
}

export async function createI18n(language: Language): Promise<i18n> {
  const instance = i18next.createInstance();
  await instance.init({
    lng: language,
    fallbackLng: "en",
    resources,
    interpolation: { escapeValue: false },
  });
  return instance;
}
