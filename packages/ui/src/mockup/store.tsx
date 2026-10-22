import { createContext, use } from "react";
import type { DetailsTab } from "../components/DetailsPanel";
import type { Place } from "../lib/places";
import type { ShareLink, SharePerson } from "../components/ShareDialog";
import type { UploadItem } from "../components/UploadTray";
import type { FileVersion } from "../components/VersionHistory";
import type { Sort } from "../lib/files";
import type { Node } from "./data";

export type Route =
  | { screen: "files"; place: Place; folderId: string | null }
  | { screen: "search"; query: string }
  | { screen: "viewer"; fileId: string; back: Route }
  | { screen: "admin" };

export type MockupState = {
  route: Route;
  navigate: (route: Route) => void;
  nodes: Node[];
  setNodes: (update: (nodes: Node[]) => Node[]) => void;
  selected: string[];
  setSelected: (ids: string[]) => void;
  view: "grid" | "list";
  setView: (view: "grid" | "list") => void;
  sort: Sort;
  setSort: (sort: Sort) => void;
  details: { open: boolean; tab: DetailsTab };
  setDetails: (details: { open: boolean; tab: DetailsTab }) => void;
  uploads: UploadItem[];
  setUploads: (update: (uploads: UploadItem[]) => UploadItem[]) => void;
  startUpload: (files: { name: string; size: number; url?: string }[]) => void;
  share: { itemId: string | null; people: SharePerson[]; link: ShareLink };
  setShare: (share: MockupState["share"]) => void;
  versions: FileVersion[];
  setVersions: (versions: FileVersion[]) => void;
  /** Runs a file action from a menu or the action bar. */
  act: (action: string, ids: string[]) => void;
};

export const MockupContext = createContext<MockupState | null>(null);

export function useMockup(): MockupState {
  const state = use(MockupContext);
  if (!state) throw new Error("useMockup needs the MockupApp");
  return state;
}

export const folderPath = (nodes: Node[], folderId: string | null) => {
  const path: Node[] = [];
  let id = folderId;
  while (id) {
    const node = nodes.find((n) => n.id === id);
    if (!node) break;
    path.unshift(node);
    id = node.parentId;
  }
  return path;
};
