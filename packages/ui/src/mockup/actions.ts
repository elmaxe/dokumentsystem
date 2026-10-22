import type { TFunction } from "i18next";
import type { MenuAction } from "../components/Menu";
import { isFolder } from "../lib/files";
import type { Node } from "./data";

export const fileMenu = (t: TFunction, item: Node): MenuAction[] =>
  item.trashedDaysLeft !== undefined
    ? [
        { id: "restore", icon: "restore_from_trash", label: t("actions.restore") },
        {
          id: "deleteForever",
          icon: "delete_forever",
          label: t("actions.deleteForever"),
          danger: true,
        },
      ]
    : [
        { id: "open", icon: isFolder(item) ? "folder_open" : "open_in_new", label: t("actions.open") },
        { id: "share", icon: "person_add", label: t("actions.share"), separatorBefore: true },
        { id: "download", icon: "download", label: t("actions.download") },
        { id: "rename", icon: "edit", label: t("actions.rename") },
        { id: "move", icon: "drive_file_move", label: t("actions.move") },
        item.starred
          ? { id: "unstar", icon: "star", label: t("actions.unstar") }
          : { id: "star", icon: "star", label: t("actions.star") },
        { id: "details", icon: "info", label: t("actions.details") },
        { id: "trash", icon: "delete", label: t("actions.trash"), separatorBefore: true },
      ];

export const selectionActions = (t: TFunction, trash: boolean): MenuAction[] =>
  trash
    ? [
        { id: "restore", icon: "restore_from_trash", label: t("actions.restore") },
        { id: "deleteForever", icon: "delete_forever", label: t("actions.deleteForever") },
      ]
    : [
        { id: "share", icon: "person_add", label: t("actions.share") },
        { id: "download", icon: "download", label: t("actions.download") },
        { id: "move", icon: "drive_file_move", label: t("actions.move") },
        { id: "trash", icon: "delete", label: t("actions.trash") },
      ];

export const selectionMore = (t: TFunction): MenuAction[] => [
  { id: "star", icon: "star", label: t("actions.star") },
  { id: "rename", icon: "edit", label: t("actions.rename") },
  { id: "details", icon: "info", label: t("actions.details") },
];

const extensions: [RegExp, Node["kind"]][] = [
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

export const kindOf = (name: string): Node["kind"] =>
  extensions.find(([pattern]) => pattern.test(name))?.[1] ?? "other";
