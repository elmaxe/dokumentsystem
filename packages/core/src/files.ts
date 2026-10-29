// File kinds, sorting and preview types, shared by web and mobile (docs/design.md §15.1, §16.4).

/** The MIME groups that get their own icon and colour (docs/design.md §16.2). */
export type FileKind =
  | "folder"
  | "pdf"
  | "doc"
  | "sheet"
  | "slides"
  | "image"
  | "video"
  | "audio"
  | "archive"
  | "text"
  | "other";

/** The fields sorting needs. */
export type Sortable = { name: string; kind: FileKind; owner?: string; modified: string; size?: number };

export const isFolder = (item: { kind: FileKind }) => item.kind === "folder";

/** Pages rather than pictures: their thumbnails show the top of page 1, as a reader would see it. */
export const isPaged = (item: { kind: FileKind }) => ["pdf", "doc", "sheet", "slides", "text"].includes(item.kind);

export const kindIcons: Record<FileKind, string> = {
  folder: "folder",
  pdf: "picture_as_pdf",
  doc: "description",
  sheet: "table_chart",
  slides: "slideshow",
  image: "image",
  video: "movie",
  audio: "music_note",
  archive: "folder_zip",
  text: "article",
  other: "draft",
};

/** Folders first, then files, each group sorted by the chosen key (§16.4). */
export type SortKey = "name" | "owner" | "modified" | "size";
export type Sort = { key: SortKey; dir: "asc" | "desc" };

export function sortItems<T extends Sortable>(items: T[], sort: Sort, locale: string): T[] {
  const collator = new Intl.Collator(locale, { numeric: true, sensitivity: "base" });
  const sign = sort.dir === "asc" ? 1 : -1;
  const compare = (a: Sortable, b: Sortable) => {
    switch (sort.key) {
      case "name":
        return collator.compare(a.name, b.name);
      case "owner":
        return collator.compare(a.owner ?? "", b.owner ?? "") || collator.compare(a.name, b.name);
      case "modified":
        return a.modified.localeCompare(b.modified);
      case "size":
        return (a.size ?? 0) - (b.size ?? 0);
    }
  };
  return [...items].sort(
    (a, b) => Number(isFolder(b)) - Number(isFolder(a)) || sign * compare(a, b),
  );
}

const extensions: [RegExp, FileKind][] = [
  [/\.pdf$/i, "pdf"],
  [/\.(docx?|odt|rtf|pages)$/i, "doc"],
  [/\.(xlsx?|ods|csv|numbers)$/i, "sheet"],
  [/\.(pptx?|odp|key)$/i, "slides"],
  [/\.(jpe?g|png|gif|webp|heic|avif|dng|svg)$/i, "image"],
  [/\.(mp4|m4v|mov|mkv|webm|avi|wmv|mpe?g|mts|m2ts|flv|3gp|3g2)$/i, "video"],
  [/\.(mp3|m4a|aac|flac|wav|aiff?|ogg|oga|opus|wma|amr|awb|caf|ape|wv)$/i, "audio"],
  [/\.(zip|7z|rar|tar|gz)$/i, "archive"],
  [/\.(txt|md|json|xml|log)$/i, "text"],
];

const mimeKinds: [RegExp, FileKind][] = [
  [/^application\/pdf$/, "pdf"],
  [/^image\//, "image"],
  [/^video\//, "video"],
  [/^audio\//, "audio"],
  [/^text\//, "text"],
];

/** The icon group for a file, from its name, falling back to its MIME type. */
export const kindOf = (name: string, mimeType?: string | null): FileKind =>
  extensions.find(([pattern]) => pattern.test(name))?.[1] ??
  (mimeType ? mimeKinds.find(([pattern]) => pattern.test(mimeType))?.[1] : undefined) ??
  "other";

/** How the viewer shows a file (docs/design.md §14). Office formats get the PDF viewer once Gotenberg renditions exist. */
export type PreviewType = "image" | "pdf" | "text" | "markdown" | "video" | "audio" | "none";

const textTypes = /^(text\/|application\/(json|xml|javascript|x-yaml|yaml|toml|x-sh|sql|ld\+json)$)|\+(json|xml)$/;


export function previewTypeOf(mimeType?: string | null): PreviewType {
  if (!mimeType) return "none";
  if (mimeType === "application/pdf") return "pdf";
  if (mimeType.startsWith("image/")) return "image";
  // Whether the original plays, or waits for a playable copy, is the server's call (`NodeDto.media`, §14).
  if (mimeType.startsWith("video/")) return "video";
  if (mimeType.startsWith("audio/")) return "audio";
  if (mimeType === "text/markdown" || mimeType === "text/x-markdown") return "markdown";
  if (textTypes.test(mimeType)) return "text";
  return "none";
}
