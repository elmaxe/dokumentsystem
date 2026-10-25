import type { TFunction } from "i18next";

/** How much of a date is known: "1974", "1974-07", "1974-07-14" or "1974-07-14 15:30". */
export type DatePrecision = "year" | "month" | "day" | "time";

/** A date taken, as the server writes it; the time is the camera's clock (§13.5). */
export type TakenView = { value: string; precision: DatePrecision; offsetMinutes?: number | null };

/** A detail people can edit: what to show, what the file says, and whether someone changed it. */
export type EditableView<T> = { value: T | null; read: T | null; edited: boolean };

/** The Details section of a file's info (§13.5). The shape of the API's FileDetailsDto. */
export type FileDetailsView = {
  reading: boolean;
  title: EditableView<string>;
  description: EditableView<string>;
  taken: EditableView<TakenView>;
  camera?: string | null;
  lens?: string | null;
  width?: number | null;
  height?: number | null;
  durationSeconds?: number | null;
  pageCount?: number | null;
  place?: { latitude: number; longitude: number } | null;
  /** Other read-only details by key: subject, author, artist, composer, album, genre, company, keywords, comment. */
  fields: { key: string; value: string }[];
  editedBy?: string | null;
  editedAt?: string | null;
};

/** What a details edit sends: null keeps what the file says. */
export type DetailsEdit = { title: string | null; description: string | null; taken: string | null };

export type TextPageSource = "text" | "ocr" | "metadata" | "added";

export type TextPageView = {
  pageNo: number;
  source: TextPageSource;
  text: string;
  edited: boolean;
  editedBy?: string | null;
  editedAt?: string | null;
};

/** The Text section of a file's info. The shape of the API's FileTextDto. */
export type FileTextView = {
  state: "reading" | "ready" | "empty" | "unsupported" | "unreadable";
  ocrSkipped: boolean;
  pageCount?: number | null;
  totalPages: number;
  maxPage: number;
  words: number;
  editedPages: number;
  pages: TextPageView[];
  nextFrom?: number | null;
};

/** The edit that keeps every edited detail as it is: what to send to change just one. */
export const currentEdit = (details: FileDetailsView): DetailsEdit => ({
  title: details.title.edited ? (details.title.value ?? "") : null,
  description: details.description.edited ? (details.description.value ?? "") : null,
  taken: details.taken.edited ? (details.taken.value?.value ?? null) : null,
});

/** The forms the server reads for a date taken. */
export const takenPattern = /^\d{4}(-(0[1-9]|1[0-2])(-(0[1-9]|[12]\d|3[01])([ T]([01]\d|2[0-3]):[0-5]\d(:[0-5]\d)?)?)?)?$/;

/** "1974", "July 1974", "14 July 1974", or "14 July 1974 at 15:30 (UTC+2)", in the UI language. */
export function formatTaken(t: TFunction, language: string, taken: TakenView): string {
  const [date, time = "00:00"] = taken.value.split(/[ T]/);
  const [year, month = 1, day = 1] = (date ?? "").split("-").map(Number);
  const [hour = 0, minute = 0] = time.split(":").map(Number);
  const at = new Date(Date.UTC(year ?? 1970, month - 1, day, hour, minute));
  const locale = language === "en" ? "en-GB" : language;
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

/** "3:07" or "1:02:45". */
export function formatDuration(seconds: number): string {
  const total = Math.round(seconds);
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = String(total % 60).padStart(2, "0");
  return h > 0 ? `${h}:${String(m).padStart(2, "0")}:${s}` : `${m}:${s}`;
}
