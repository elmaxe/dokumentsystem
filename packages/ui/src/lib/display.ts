import { formatBytes, formatFileDate, formatFullDate } from "@dokument/core";
import { createContext, useContext } from "react";
import { useTranslation } from "react-i18next";

/** The person's display settings from the account menu (§16.5). */
export type DisplaySettings = {
  /** Times to the second ("09:14:07") instead of to the minute. */
  showSeconds: boolean;
};

const DisplayContext = createContext<DisplaySettings>({ showSeconds: false });

/** Wrap the app in this so every list, panel and dialog formats dates the way the person chose. */
export const DisplaySettingsProvider = DisplayContext.Provider;

/** Sizes and dates in the UI language and the person's display settings. */
export function useFormat() {
  const { i18n } = useTranslation();
  const { showSeconds } = useContext(DisplayContext);
  const language = i18n.language;
  return {
    bytes: (bytes: number) => formatBytes(bytes, language),
    date: (value: Date | string, options: { inSentence?: boolean } = {}) =>
      formatFileDate(value, language, { ...options, seconds: showSeconds }),
    fullDate: (value: Date | string) => formatFullDate(value, language, { seconds: showSeconds }),
  };
}
