import type { DetailsTab } from "@dokument/ui";
import { useSyncExternalStore } from "react";

export type DetailsPanelState = { open: boolean; tab: DetailsTab };

/**
 * Whether the details panel is open, and on which tab. One state for the file list and the viewer, so a panel left
 * open stays open when a file is opened, while stepping through files and back in the list (§15.2). It lasts for the
 * tab's session; a reload starts closed.
 */
let state: DetailsPanelState = { open: false, tab: "info" };
const listeners = new Set<() => void>();

const subscribe = (listener: () => void) => {
  listeners.add(listener);
  return () => void listeners.delete(listener);
};

const setDetailsPanel = (next: DetailsPanelState) => {
  if (next.open === state.open && next.tab === state.tab) return;
  state = next;
  listeners.forEach((listener) => listener());
};

export const useDetailsPanel = () =>
  [useSyncExternalStore(subscribe, () => state), setDetailsPanel] as const;
