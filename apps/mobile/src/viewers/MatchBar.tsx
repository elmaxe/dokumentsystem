import { useTranslation } from "react-i18next";
import { View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { IconButton } from "../ui/Button";
import { Text } from "../ui/Text";

type Step = { label: string; onPress?: () => void };

/**
 * The bar a file opened from search shows at the bottom (docs/design.md §13.3): what's marked, where the person is
 * among the pages or moments that match, steps between them, and a way to stop marking.
 */
export function MatchBar({
  query,
  detail,
  previous,
  next,
  onClose,
  top,
}: {
  query: string;
  detail?: string;
  /** Without `onPress`, the step is there but can't be taken, as at the first page with matches. */
  previous?: Step;
  next?: Step;
  onClose: () => void;
  /** Above the content instead of below it, clear of a player's own controls. */
  top?: boolean;
}) {
  const { t } = useTranslation();
  const insets = useSafeAreaInsets();
  return (
    <View
      pointerEvents="box-none"
      style={top ? { top: 12 } : { bottom: 12 + insets.bottom }}
      className="absolute left-3 right-3"
    >
      <View
        accessibilityLiveRegion="polite"
        className="min-h-14 flex-row items-center gap-1 rounded-lg bg-surface-container-high py-1 pl-4 pr-1 shadow-elevation-3"
      >
        <View className="min-w-0 flex-1">
          <Text variant="title-sm" numberOfLines={1} className="text-on-surface">
            {t("viewer.matches", { query })}
          </Text>
          {detail ? (
            <Text variant="body-sm" numberOfLines={1} className="text-on-surface-variant">
              {detail}
            </Text>
          ) : null}
        </View>
        {previous ? (
          <IconButton icon="keyboard_arrow_up" label={previous.label} onPress={previous.onPress} disabled={!previous.onPress} />
        ) : null}
        {next ? (
          <IconButton icon="keyboard_arrow_down" label={next.label} onPress={next.onPress} disabled={!next.onPress} />
        ) : null}
        <IconButton icon="close" label={t("viewer.stopMarking")} onPress={onClose} />
      </View>
    </View>
  );
}
