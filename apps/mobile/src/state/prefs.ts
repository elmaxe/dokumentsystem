import AsyncStorage from "@react-native-async-storage/async-storage";
import type { Sort } from "@dokument/core";
import { useSyncExternalStore } from "react";

/** How file lists look, kept on the phone like the web keeps it per browser (docs/design.md §16.4). */
export type ListPrefs = { view: "grid" | "list"; sort: Sort };

const storageKey = "dokument.list";
let prefs: ListPrefs = { view: "grid", sort: { key: "name", dir: "asc" } };
const listeners = new Set<() => void>();

AsyncStorage.getItem(storageKey)
  .then((stored) => {
    if (!stored) return;
    prefs = { ...prefs, ...(JSON.parse(stored) as Partial<ListPrefs>) };
    listeners.forEach((listener) => listener());
  })
  .catch(() => {});

export function setListPrefs(next: Partial<ListPrefs>) {
  prefs = { ...prefs, ...next };
  listeners.forEach((listener) => listener());
  void AsyncStorage.setItem(storageKey, JSON.stringify(prefs)).catch(() => {});
}

export const useListPrefs = () =>
  useSyncExternalStore(
    (listener) => {
      listeners.add(listener);
      return () => void listeners.delete(listener);
    },
    () => prefs,
  );
