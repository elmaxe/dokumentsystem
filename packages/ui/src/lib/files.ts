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

export type FileItem = {
  id: string;
  name: string;
  kind: FileKind;
  /** Bytes. Folders have none. */
  size?: number;
  /** ISO 8601. */
  modified: string;
  owner: string;
  /** A thumbnail URL. Missing means the type icon is shown instead. */
  thumbnail?: string;
  /** True while the worker is still making the thumbnail; the tile shimmers (§16.4). */
  thumbnailPending?: boolean;
  /** Video and audio length, such as "3:42". */
  duration?: string;
  shared?: boolean;
  starred?: boolean;
};

export const isFolder = (item: Pick<FileItem, "kind">) => item.kind === "folder";

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

export function sortItems<T extends FileItem>(items: T[], sort: Sort, locale: string): T[] {
  const collator = new Intl.Collator(locale, { numeric: true, sensitivity: "base" });
  const sign = sort.dir === "asc" ? 1 : -1;
  const compare = (a: FileItem, b: FileItem) => {
    switch (sort.key) {
      case "name":
        return collator.compare(a.name, b.name);
      case "owner":
        return collator.compare(a.owner, b.owner) || collator.compare(a.name, b.name);
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
  [/\.(mp4|mov|mkv|webm|avi)$/i, "video"],
  [/\.(mp3|m4a|flac|wav|ogg|opus)$/i, "audio"],
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
