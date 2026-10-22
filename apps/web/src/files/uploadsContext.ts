import type { UploadItem } from "@dokument/ui";
import { createContext, use } from "react";

/** Where picked files go: into a folder (null is the top of My files), or as a new version of one file. */
export type UploadTarget =
  | { folderId: string | null; folderName: string; existingNames: Set<string> }
  | { nodeId: string; folderId: string | null; folderName: string };

export type Uploads = {
  items: UploadItem[];
  start: (files: File[], target: UploadTarget) => void;
  pause: (id: string) => void;
  resume: (id: string) => void;
  retry: (id: string) => void;
  cancel: (id: string) => void;
  clear: () => void;
  /** The file an upload produced, once done. */
  resultOf: (id: string) => { nodeId?: string; folderId: string | null } | undefined;
};

export const UploadsContext = createContext<Uploads | null>(null);

export function useUploads(): Uploads {
  const uploads = use(UploadsContext);
  if (!uploads) throw new Error("useUploads needs an UploadProvider");
  return uploads;
}

