/** The places in the main navigation (docs/design.md §15.2). */
export type Place = "files" | "shared" | "recent" | "starred" | "trash";

export const places: { id: Place; icon: string; key: string }[] = [
  { id: "files", icon: "folder", key: "nav.myFiles" },
  { id: "shared", icon: "folder_shared", key: "nav.sharedWithMe" },
  { id: "recent", icon: "schedule", key: "nav.recent" },
  { id: "starred", icon: "star", key: "nav.starred" },
  { id: "trash", icon: "delete", key: "nav.trash" },
];

/** The admin pages (docs/design.md §4.3). */
export type AdminPlace = "people" | "storage" | "settings";

export const adminPlaces: { id: AdminPlace; icon: string; key: string }[] = [
  { id: "people", icon: "group", key: "admin.nav.people" },
  { id: "storage", icon: "hard_drive", key: "admin.nav.storage" },
  { id: "settings", icon: "settings", key: "admin.nav.settings" },
];
