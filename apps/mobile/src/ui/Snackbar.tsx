import { useEffect, useSyncExternalStore } from "react";
import { Pressable, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { Text } from "./Text";

type Snack = { id: number; text: string; action?: { label: string; onPress: () => void } };

let current: Snack | null = null;
let next = 0;
const listeners = new Set<() => void>();
const set = (snack: Snack | null) => {
  current = snack;
  listeners.forEach((listener) => listener());
};

/**
 * Says what just happened, in the past tense, with an Undo where one makes sense (docs/design.md §16.5). A new one
 * replaces the last.
 */
export function toast(text: string, action?: Snack["action"]) {
  set({ id: ++next, text, action });
}

/** Shows the latest snackbar for a few seconds, above the navigation bar. Mounted once, behind sign-in. */
export function SnackbarHost() {
  const insets = useSafeAreaInsets();
  const snack = useSyncExternalStore(
    (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    () => current,
  );

  useEffect(() => {
    if (!snack) return;
    const timer = setTimeout(() => current?.id === snack.id && set(null), snack.action ? 6_000 : 4_000);
    return () => clearTimeout(timer);
  }, [snack]);

  if (!snack) return null;
  return (
    <View
      pointerEvents="box-none"
      style={{ bottom: insets.bottom + 96 }}
      className="absolute left-4 right-4 z-10"
      accessibilityLiveRegion="polite"
    >
      <View className="min-h-12 flex-row items-center gap-2 rounded-xs bg-on-surface py-1 pl-4 pr-2 shadow-elevation-3">
        <Text variant="body-md" className="flex-1 py-2 text-surface">
          {snack.text}
        </Text>
        {snack.action ? (
          <Pressable
            accessibilityRole="button"
            onPress={() => {
              set(null);
              snack.action!.onPress();
            }}
            className="min-h-touch justify-center rounded-full px-3 active:opacity-70"
          >
            <Text variant="label-lg" className="text-primary-container">
              {snack.action.label}
            </Text>
          </Pressable>
        ) : null}
      </View>
    </View>
  );
}
