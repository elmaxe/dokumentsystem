import { useTranslation } from "react-i18next";
import { cn } from "../lib/cn";
import { kindIcons, type FileKind } from "../lib/files";
import { Icon } from "./Icon";

const colors: Record<FileKind, string> = {
  folder: "text-file-folder",
  pdf: "text-file-pdf",
  doc: "text-file-doc",
  sheet: "text-file-sheet",
  slides: "text-file-slides",
  image: "text-file-image",
  video: "text-file-video",
  audio: "text-file-audio",
  archive: "text-file-archive",
  text: "text-file-text",
  other: "text-file-text",
};

export type FileTypeIconProps = {
  kind: FileKind;
  /** Shared folders get the shared-folder icon. */
  shared?: boolean;
  size?: "sm" | "md" | number;
  /** Decorative when the file name is next to it, which is almost always. */
  labelled?: boolean;
  className?: string;
};

/** A filled Material Symbol in the file type's colour. Also the thumbnail fallback (§16.4). */
export function FileTypeIcon({ kind, shared, size, labelled, className }: FileTypeIconProps) {
  const { t } = useTranslation();
  return (
    <Icon
      name={kind === "folder" && shared ? "folder_shared" : kindIcons[kind]}
      filled
      size={size}
      label={labelled ? t(`kinds.${kind}`) : undefined}
      className={cn(colors[kind], className)}
    />
  );
}
