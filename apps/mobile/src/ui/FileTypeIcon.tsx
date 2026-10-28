import { kindIcons, type FileKind } from "@dokument/core";
import type { ColorToken } from "@dokument/tokens";
import { Icon, type IconName } from "./Icon";

const colors: Record<FileKind, ColorToken> = {
  folder: "file-folder",
  pdf: "file-pdf",
  doc: "file-doc",
  sheet: "file-sheet",
  slides: "file-slides",
  image: "file-image",
  video: "file-video",
  audio: "file-audio",
  archive: "file-archive",
  text: "file-text",
  other: "file-text",
};

/** A filled Material Symbol in the file type's colour. Also the thumbnail fallback (docs/design.md §16.4). */
export function FileTypeIcon({ kind, size = 24 }: { kind: FileKind; size?: number }) {
  return <Icon name={kindIcons[kind] as IconName} filled size={size} color={colors[kind]} />;
}
