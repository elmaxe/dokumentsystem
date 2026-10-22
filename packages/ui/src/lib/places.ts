/** The places in the main navigation (docs/design.md §15.2). */
export type Place = "files" | "shared" | "recent" | "starred" | "trash";

export const places: { id: Place; icon: string; key: string }[] = [
  { id: "files", icon: "folder", key: "nav.myFiles" },
  { id: "shared", icon: "folder_shared", key: "nav.sharedWithMe" },
  { id: "recent", icon: "schedule", key: "nav.recent" },
  { id: "starred", icon: "star", key: "nav.starred" },
  { id: "trash", icon: "delete", key: "nav.trash" },
];
