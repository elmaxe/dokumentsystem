import type { UploadGroup, UploadItem } from "@dokument/ui";
import { createContext, use } from "react";
import type { PickedFolder } from "./folderPicking";

/** Where picked files go: into a folder (null is the top of My files). */
export type FolderTarget = { folderId: string | null; folderName: string; existingNames: Set<string> };

/** Where picked files go: into a folder, or as a new version of one file. */
export type UploadTarget = FolderTarget | { nodeId: string; folderId: string | null; folderName: string };

export type Uploads = {
  items: UploadItem[];
  /** Folder uploads; their files are the items with that group. */
  groups: UploadGroup[];
  start: (files: File[], target: UploadTarget) => void;
  /** Uploads whole folders, one after another (§8.1). */
  startFolders: (folders: PickedFolder[], target: FolderTarget) => void;
  pause: (id: string) => void;
  resume: (id: string) => void;
  retry: (id: string) => void;
  cancel: (id: string) => void;
  pauseGroup: (group: string) => void;
  resumeGroup: (group: string) => void;
  retryGroup: (group: string) => void;
  dismissGroup: (group: string) => void;
  clear: () => void;
  /** The file an upload produced, once done. */
  resultOf: (id: string) => { nodeId?: string; folderId: string | null } | undefined;
  /** The top folder a folder upload made or merged into, once it exists. */
  folderOf: (group: string) => string | undefined;
};

export const UploadsContext = createContext<Uploads | null>(null);

export function useUploads(): Uploads {
  const uploads = use(UploadsContext);
  if (!uploads) throw new Error("useUploads needs an UploadProvider");
  return uploads;
}
