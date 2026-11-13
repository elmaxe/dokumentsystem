import type { TakenView } from "@dokument/core";

export { formatTaken, type DatePrecision, type TakenView } from "@dokument/core";

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

export type TextPageSource = "text" | "ocr" | "metadata" | "added" | "transcript";

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

/** "3:07" or "1:02:45". */
export function formatDuration(seconds: number): string {
  const total = Math.round(seconds);
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = String(total % 60).padStart(2, "0");
  return h > 0 ? `${h}:${String(m).padStart(2, "0")}:${s}` : `${m}:${s}`;
}
