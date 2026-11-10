import { useEffect, useState, type ReactNode } from "react";
import { Keyboard, Modal, Platform, Pressable, useWindowDimensions, View } from "react-native";
import Animated, {
  Easing,
  useAnimatedStyle,
  useSharedValue,
  withTiming,
} from "react-native-reanimated";
import { scheduleOnRN } from "react-native-worklets";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { useTranslation } from "react-i18next";
import { useTheme } from "../state/theme";
import { Text } from "./Text";

/**
 * A bottom sheet, which replaces dialogs and menus on phones (docs/design.md §16.3). The scrim fades in while the
 * sheet slides up from below, and both go the other way on close; the Modal itself doesn't animate, or the scrim
 * would slide up with the sheet.
 */
export function Sheet({
  open,
  onClose,
  title,
  children,
}: {
  open: boolean;
  onClose: () => void;
  title?: string;
  children: ReactNode;
}) {
  const insets = useSafeAreaInsets();
  const { t } = useTranslation();
  const theme = useTheme();
  const screen = useWindowDimensions();
  // Stays mounted while it closes, so the sheet can slide away before the Modal goes.
  const [mounted, setMounted] = useState(open);
  const progress = useSharedValue(0);
  const height = useSharedValue(screen.height);
  // How far the keyboard covers the sheet: a Modal isn't resized for it, so the sheet moves up by itself.
  const keyboard = useSharedValue(0);

  if (open && !mounted) setMounted(true);
  useEffect(() => {
    animate(progress, open, () => setMounted(false));
  }, [open, progress]);
  useEffect(() => {
    if (!mounted) return;
    const ios = Platform.OS === "ios";
    const shown = Keyboard.addListener(ios ? "keyboardWillShow" : "keyboardDidShow", (event) =>
      lift(keyboard, Math.max(0, event.endCoordinates.height - insets.bottom), event.duration),
    );
    const hidden = Keyboard.addListener(ios ? "keyboardWillHide" : "keyboardDidHide", (event) =>
      lift(keyboard, 0, event.duration),
    );
    return () => {
      shown.remove();
      hidden.remove();
    };
  }, [mounted, keyboard, insets.bottom]);

  const scrim = useAnimatedStyle(() => ({ opacity: progress.value }));
  const sheet = useAnimatedStyle(() => ({
    transform: [{ translateY: (1 - progress.value) * height.value - keyboard.value }],
  }));

  return (
    <Modal
      visible={mounted}
      transparent
      animationType="none"
      onRequestClose={onClose}
      statusBarTranslucent
    >
      {/* A Modal renders outside the themed tree, so it sets the theme's variables again. */}
      <View style={[{ flex: 1 }, theme.vars]} className="justify-end">
        {/* Animated views take styles, not class names, so the classes go on plain views inside them. */}
        <Animated.View
          style={[{ position: "absolute", top: 0, right: 0, bottom: 0, left: 0 }, scrim]}
        >
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t("common.close")}
            onPress={onClose}
            className="flex-1 bg-scrim"
          />
        </Animated.View>
        <Animated.View
          style={sheet}
          onLayout={(event) => {
            // Measured while open, so content that empties as it closes doesn't change how far it slides.
            if (open) height.value = event.nativeEvent.layout.height;
          }}
        >
          <View
            style={{ paddingBottom: insets.bottom + 16 }}
            className="rounded-t-lg bg-surface-container-high pt-2 shadow-elevation-3"
            accessibilityViewIsModal
          >
            <View className="mb-3 h-1 w-8 self-center rounded-full bg-outline" />
            {title ? (
              <Text
                variant="title-lg"
                accessibilityRole="header"
                className="mx-5 mb-2 text-on-surface"
              >
                {title}
              </Text>
            ) : null}
            {children}
          </View>
        </Animated.View>
      </View>
    </Modal>
  );
}

/**
 * The last value that wasn't null, so a sheet opened for something (`open={!!chosen}`) still shows it while it
 * slides away after `chosen` went back to null.
 */
export function useKept<T>(value: T | null): T | null {
  const [kept, setKept] = useState(value);
  if (value !== null && value !== kept) setKept(value);
  return value ?? kept;
}

function lift(keyboard: { value: number }, to: number, duration: number) {
  keyboard.value = withTiming(to, { duration: duration > 0 ? duration : 200 });
}

// Outside the component, where React's rules see a plain object rather than a hook's value.
function animate(progress: { value: number }, open: boolean, closed: () => void) {
  progress.value = open
    ? withTiming(1, { duration: 250, easing: Easing.out(Easing.cubic) })
    : withTiming(0, { duration: 200, easing: Easing.in(Easing.cubic) }, (finished) => {
        if (finished) scheduleOnRN(closed);
      });
}

/** One action in a sheet: an icon and a verb, 48 px tall. */
export function SheetAction({
  icon,
  label,
  onPress,
  danger,
  selected,
}: {
  icon: ReactNode;
  label: string;
  onPress: () => void;
  danger?: boolean;
  /** For a choice among several, such as the sort order. */
  selected?: boolean;
}) {
  return (
    <Pressable
      accessibilityRole={selected === undefined ? "button" : "radio"}
      accessibilityState={selected === undefined ? undefined : { checked: selected }}
      onPress={onPress}
      className="min-h-touch flex-row items-center gap-4 px-5 active:bg-surface-container-highest"
    >
      {icon}
      <Text className={`flex-1 ${danger ? "text-error" : "text-on-surface"}`}>{label}</Text>
    </Pressable>
  );
}
