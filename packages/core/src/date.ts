const day = 24 * 60 * 60 * 1000;

// Plain English uses British order and a 24-hour clock ("12 Mar", "09:14"), as in the design examples.
const localeOf = (language: string) => (language === "en" ? "en-GB" : language);

// Spelled out rather than from Intl.RelativeTimeFormat, which Hermes on Android doesn't have. CLDR's words.
const relativeDays: Record<string, [today: string, yesterday: string]> = {
  sv: ["i dag", "i går"],
  en: ["today", "yesterday"],
};

const startOfDay = (date: Date) => new Date(date.getFullYear(), date.getMonth(), date.getDate());

/**
 * Formats a file date the way lists show it: the time today ("Today 09:14"), "Yesterday",
 * day and month within the year ("12 Mar"), and the full date before that ("21 Jun 2025").
 * Inside a sentence ("Modified today 09:14"), pass `{ inSentence: true }` to keep it lower case, and
 * `{ seconds: true }` for people who show seconds ("Today 09:14:07").
 */
export function formatFileDate(
  value: Date | string,
  language: string,
  {
    now = new Date(),
    inSentence = false,
    seconds = false,
  }: { now?: Date; inSentence?: boolean; seconds?: boolean } = {},
): string {
  const locale = localeOf(language);
  const date = typeof value === "string" ? new Date(value) : value;
  const days = Math.round((startOfDay(now).getTime() - startOfDay(date).getTime()) / day);

  if (days === 0 || days === 1) {
    const relative = (relativeDays[language] ?? relativeDays.en!)[days]!;
    const word = inSentence
      ? relative
      : relative.charAt(0).toLocaleUpperCase(locale) + relative.slice(1);
    if (days === 1) return word;
    const time = new Intl.DateTimeFormat(locale, {
      hour: "2-digit",
      minute: "2-digit",
      second: seconds ? "2-digit" : undefined,
    }).format(date);
    return `${word} ${time}`;
  }

  const sameYear = date.getFullYear() === now.getFullYear();
  return new Intl.DateTimeFormat(locale, {
    day: "numeric",
    month: "short",
    year: sameYear ? undefined : "numeric",
  }).format(date);
}

/**
 * The exact moment, for tooltips and details where the short list form isn't enough: "23 October 2026, 17:40",
 * or "23 October 2026, 17:40:12" with `{ seconds: true }`.
 */
export function formatFullDate(
  value: Date | string,
  language: string,
  { seconds = false }: { seconds?: boolean } = {},
): string {
  const date = typeof value === "string" ? new Date(value) : value;
  return new Intl.DateTimeFormat(localeOf(language), {
    day: "numeric",
    month: "long",
    year: "numeric",
    hour: "2-digit",
    minute: "2-digit",
    second: seconds ? "2-digit" : undefined,
  }).format(date);
}
