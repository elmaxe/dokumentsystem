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

/** How much of a date is known: "1974", "1974-07", "1974-07-14" or "1974-07-14 15:30". */
export type DatePrecision = "year" | "month" | "day" | "time";

/** A date taken, as the server writes it; the time is the camera's clock (§13.5). */
export type TakenView = { value: string; precision: DatePrecision; offsetMinutes?: number | null };

/** "1974", "July 1974", "14 July 1974", or "14 July 1974 at 15:30 (UTC+2)", in the UI language. */
export function formatTaken(
  t: (key: "fileInfo.utcOffset", options: { offset: string }) => string,
  language: string,
  taken: TakenView,
): string {
  const [date, time = "00:00"] = taken.value.split(/[ T]/);
  const [year, month = 1, day = 1] = (date ?? "").split("-").map(Number);
  const [hour = 0, minute = 0] = time.split(":").map(Number);
  const at = new Date(Date.UTC(year ?? 1970, month - 1, day, hour, minute));
  const locale = localeOf(language);
  const options: Intl.DateTimeFormatOptions = { timeZone: "UTC" };
  switch (taken.precision) {
    case "year":
      return String(year);
    case "month":
      return new Intl.DateTimeFormat(locale, { ...options, month: "long", year: "numeric" }).format(at);
    case "day":
      return new Intl.DateTimeFormat(locale, { ...options, day: "numeric", month: "long", year: "numeric" }).format(at);
  }

  const text = new Intl.DateTimeFormat(locale, {
    ...options,
    day: "numeric",
    month: "long",
    year: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  }).format(at);
  return taken.offsetMinutes == null ? text : `${text} (${t("fileInfo.utcOffset", { offset: offsetLabel(taken.offsetMinutes) })})`;
}

/** +2, −5, +5:30, or ±0. */
function offsetLabel(minutes: number): string {
  if (minutes === 0) return "±0";
  const sign = minutes > 0 ? "+" : "−";
  const abs = Math.abs(minutes);
  const rest = abs % 60;
  return `${sign}${Math.floor(abs / 60)}${rest ? `:${String(rest).padStart(2, "0")}` : ""}`;
}
