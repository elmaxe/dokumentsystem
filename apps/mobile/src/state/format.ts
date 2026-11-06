import { formatBytes, formatFileDate } from "@dokument/core";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { sessionQuery } from "./queries";

/** Dates and sizes in the person's language, with seconds when they've asked for them (docs/design.md §16.5). */
export function useFormat() {
  const { i18n } = useTranslation();
  const session = useQuery(sessionQuery);
  const seconds = session.data?.user.showSeconds ?? false;
  return {
    date: (value: string | Date, inSentence = false) => formatFileDate(value, i18n.language, { seconds, inSentence }),
    size: (bytes: number) => formatBytes(bytes, i18n.language),
  };
}
