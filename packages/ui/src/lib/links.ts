/** The last day a link works, for an expiry stored as the start of the day after it, in local time (§9). */
export function lastDay(expiresAt: string): Date {
  return new Date(new Date(expiresAt).getTime() - 1);
}

/** "30 Nov 2026" in the UI language. */
export function dayLabel(day: Date, language: string): string {
  return new Intl.DateTimeFormat(language, { day: "numeric", month: "short", year: "numeric" }).format(day);
}
