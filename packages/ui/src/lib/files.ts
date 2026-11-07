import type { FileKind } from "@dokument/core";
import type { TFunction } from "i18next";
import type { TagInfo } from "../components/Tags";
import type { ActiveProcessingStep } from "./processing";

export type FileItem = {
  id: string;
  name: string;
  kind: FileKind;
  /** Bytes; for a folder, everything in it. */
  size?: number;
  /** What a folder holds, at any depth. */
  contents?: FolderContents;
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
  /** Shared with the person and not opened yet: marked New in Shared with me. */
  isNew?: boolean;
  /** The file's tags (§13.4). */
  tags?: TagInfo[];
  /** Just appeared because of a change made elsewhere: briefly outlined so it's noticed (§15.4). */
  arrived?: boolean;
  /** Steps the worker still has to do; while there are any the file shows a "Processing" badge (§16.4). */
  processing?: ActiveProcessingStep[];
};

export type FolderContents = { files: number; folders: number };

/** "1,204 files, 38 folders", "1 file", or "Empty". */
export function contentsLabel(t: TFunction, { files, folders }: FolderContents): string {
  if (files === 0 && folders === 0) return t("files.contents.empty");
  return [
    files > 0 && t("files.contents.files", { count: files }),
    folders > 0 && t("files.contents.folders", { count: folders }),
  ]
    .filter(Boolean)
    .join(", ");
}

export {
  isFolder,
  isPaged,
  kindIcons,
  kindOf,
  previewTypeOf,
  sortItems,
  type FileKind,
  type PreviewType,
  type Sort,
  type SortKey,
} from "@dokument/core";
