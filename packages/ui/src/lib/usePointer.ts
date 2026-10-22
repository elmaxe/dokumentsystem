import { useSyncExternalStore } from "react";

const query = "(pointer: coarse)";

function subscribe(onChange: () => void) {
  const media = window.matchMedia(query);
  media.addEventListener("change", onChange);
  return () => media.removeEventListener("change", onChange);
}

/**
 * True on touch screens. Touch replaces right-click and rubber-band selection with long-press,
 * and double-click with tap (docs/design.md §16.3).
 */
export function useCoarsePointer(): boolean {
  return useSyncExternalStore(
    subscribe,
    () => window.matchMedia(query).matches,
    () => false,
  );
}
