const day = 24 * 60 * 60 * 1000;

const startOfDay = (date: Date) => new Date(date.getFullYear(), date.getMonth(), date.getDate());

/**
 * Formats a file date the way lists show it: the time today ("Today 09:14"), "Yesterday",
 * day and month within the year ("12 Mar"), and the full date before that ("21 Jun 2025").
 * Inside a sentence ("Modified today 09:14"), pass `{ inSentence: true }` to keep it lower case.
 */
export function formatFileDate(
  value: Date | string,
  language: string,
  { now = new Date(), inSentence = false }: { now?: Date; inSentence?: boolean } = {},
): string {
  // Plain English uses British order and a 24-hour clock ("12 Mar", "09:14"), as in the design examples.
  const locale = language === "en" ? "en-GB" : language;
  const date = typeof value === "string" ? new Date(value) : value;
  const days = Math.round((startOfDay(now).getTime() - startOfDay(date).getTime()) / day);

  if (days === 0 || days === 1) {
    const relative = new Intl.RelativeTimeFormat(locale, { numeric: "auto" }).format(-days, "day");
    const word = inSentence
      ? relative
      : relative.charAt(0).toLocaleUpperCase(locale) + relative.slice(1);
    if (days === 1) return word;
    const time = new Intl.DateTimeFormat(locale, { hour: "2-digit", minute: "2-digit" }).format(date);
    return `${word} ${time}`;
  }

  const sameYear = date.getFullYear() === now.getFullYear();
  return new Intl.DateTimeFormat(locale, {
    day: "numeric",
    month: "short",
    year: sameYear ? undefined : "numeric",
  }).format(date);
}
