import { useSyncExternalStore } from "react";

/** Light, dark, or whatever the device is set to (§16.5). */
export type ThemeChoice = "system" | "light" | "dark";

// The same key and attribute are read by the script in index.html, which applies the choice before first paint.
const key = "dokument.theme";
const dark = "(prefers-color-scheme: dark)";
const listeners = new Set<() => void>();
let current: ThemeChoice | undefined;

function read(): ThemeChoice {
  try {
    const value = localStorage.getItem(key);
    return value === "light" || value === "dark" ? value : "system";
  } catch {
    return "system";
  }
}

/** Sets `data-theme` on the root, which the token CSS uses to override `prefers-color-scheme` (§16.2). */
function apply(choice: ThemeChoice) {
  if (choice === "system") delete document.documentElement.dataset.theme;
  else document.documentElement.dataset.theme = choice;
}

function subscribe(onChange: () => void) {
  const media = window.matchMedia(dark);
  // Another tab changed the choice: follow it here too.
  const onStorage = (event: StorageEvent) => {
    if (event.key !== key) return;
    current = read();
    apply(current);
    onChange();
  };
  listeners.add(onChange);
  media.addEventListener("change", onChange);
  window.addEventListener("storage", onStorage);
  return () => {
    listeners.delete(onChange);
    media.removeEventListener("change", onChange);
    window.removeEventListener("storage", onStorage);
  };
}

/** The theme chosen on this device, and the one the device itself is set to, which "system" follows. */
export function useTheme() {
  const choice = useSyncExternalStore(subscribe, () => (current ??= read()), () => "system" as const);
  const systemDark = useSyncExternalStore(
    subscribe,
    () => window.matchMedia(dark).matches,
    () => false,
  );
  return {
    choice,
    device: systemDark ? ("dark" as const) : ("light" as const),
    setChoice: (next: ThemeChoice) => {
      try {
        if (next === "system") localStorage.removeItem(key);
        else localStorage.setItem(key, next);
      } catch {
        // Private mode: the choice lasts until the page is closed.
      }
      current = next;
      apply(next);
      for (const listener of listeners) listener();
    },
  };
}
