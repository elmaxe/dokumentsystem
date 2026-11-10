import { formatDuration, waveColumns, wavePaths, type Waveform } from "@dokument/core";
import { tokens } from "@dokument/tokens";
import { useMemo, useRef, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { View, type GestureResponderEvent } from "react-native";
import Svg, { Path } from "react-native-svg";
import { useTheme } from "../state/theme";
import { Text } from "../ui/Text";

/** "12:34" for a time in milliseconds. */
export const formatMs = (ms: number) => formatDuration(Math.max(0, ms) / 1000);

/** How tall the waveform is on phones, as on the web (docs/design.md §14). */
const waveHeight = 80;

/**
 * The player's timeline (docs/design.md §14): the waveform for audio that has one, otherwise a bar. Tap or drag to
 * seek: while the finger is down the playhead follows it and a chip (or the video's frame, from `preview`) shows the
 * time; the player seeks when the finger lets go. Spoken search hits are ticks along it.
 */
export function Timeline({
  lengthMs,
  timeMs,
  onSeek,
  waveform,
  ticks = [],
  overlay,
  onScrubStart,
  onScrubEnd,
  preview,
}: {
  lengthMs: number;
  timeMs: number;
  onSeek: (ms: number) => void;
  waveform?: Waveform | null;
  ticks?: number[];
  /** Over a video: light on dark, whatever the theme. */
  overlay?: boolean;
  onScrubStart?: () => void;
  onScrubEnd?: () => void;
  /** What to show above the finger while dragging, in place of the time chip; null keeps the chip. */
  preview?: (ms: number) => ReactNode | null;
}) {
  const { t } = useTranslation();
  const theme = useTheme();
  const [width, setWidth] = useState(0);
  const [dragMs, setDragMs] = useState<number | null>(null);
  const left = useRef(0);

  const shownMs = dragMs ?? timeMs;
  const fraction = (ms: number) => (lengthMs > 0 ? Math.min(1, Math.max(0, ms / lengthMs)) : 0);
  const played = fraction(shownMs) * width;
  const msAt = (pageX: number) => fraction(((pageX - left.current) / Math.max(1, width)) * lengthMs) * lengthMs;

  const colors = overlay
    ? { played: tokens.color.primary.dark, rest: "rgba(255,255,255,0.35)", head: "#ffffff", tick: tokens.color["warning-container"].dark }
    : {
        played: theme.color("primary"),
        rest: theme.color("outline"),
        head: theme.color("on-surface"),
        tick: theme.color("warning"),
      };

  const start = (event: GestureResponderEvent) => {
    if (!lengthMs) return;
    // The children don't take touches, so the location is within the timeline itself.
    left.current = event.nativeEvent.pageX - event.nativeEvent.locationX;
    setDragMs(msAt(event.nativeEvent.pageX));
    onScrubStart?.();
  };
  const end = (seek: boolean) => {
    if (seek && dragMs !== null) onSeek(dragMs);
    setDragMs(null);
    onScrubEnd?.();
  };
  const step = (seconds: number) => onSeek(Math.min(lengthMs, Math.max(0, timeMs + seconds * 1000)));
  const framed = dragMs !== null ? preview?.(dragMs) : null;

  return (
    <View
      accessible
      accessibilityRole="adjustable"
      accessibilityLabel={t("player.position")}
      accessibilityValue={{
        min: 0,
        max: Math.round(lengthMs / 1000),
        now: Math.round(timeMs / 1000),
        text: t("player.positionText", { at: formatMs(timeMs), total: formatMs(lengthMs) }),
      }}
      accessibilityActions={[{ name: "increment" }, { name: "decrement" }]}
      onAccessibilityAction={(event) => step(event.nativeEvent.actionName === "increment" ? 10 : -10)}
      onLayout={(event) => setWidth(event.nativeEvent.layout.width)}
      onStartShouldSetResponder={() => lengthMs > 0}
      onMoveShouldSetResponderCapture={() => dragMs !== null}
      onResponderTerminationRequest={() => false}
      onResponderGrant={start}
      onResponderMove={(event) => setDragMs(msAt(event.nativeEvent.pageX))}
      onResponderRelease={() => end(true)}
      onResponderTerminate={() => end(false)}
      style={{ height: waveform ? waveHeight : 32 }}
    >
      <View pointerEvents="none" className="absolute inset-0">
        {waveform ? (
          <>
            <WaveShape waveform={waveform} lengthMs={lengthMs} width={width} color={colors.rest} />
            <View className="absolute inset-y-0 left-0 overflow-hidden" style={{ width: played }}>
              <WaveShape waveform={waveform} lengthMs={lengthMs} width={width} color={colors.played} />
            </View>
            <View className="absolute -inset-y-1 w-0.5 rounded-full" style={{ left: played - 1, backgroundColor: colors.head }} />
            <View
              className="absolute -top-2.5 h-3.5 w-3.5 rounded-full"
              style={{
                left: played - 7,
                backgroundColor: colors.played,
                transform: [{ scale: dragMs !== null ? 1.25 : 1 }],
              }}
            />
          </>
        ) : (
          <>
            <View className="absolute inset-x-0 top-[14px] h-1 rounded-full" style={{ backgroundColor: colors.rest }}>
              <View className="h-1 rounded-full" style={{ width: played, backgroundColor: colors.played }} />
            </View>
            <View
              className="absolute top-[9px] h-3.5 w-3.5 rounded-full"
              style={{
                left: played - 7,
                backgroundColor: colors.played,
                transform: [{ scale: dragMs !== null ? 1.3 : 1 }],
              }}
            />
          </>
        )}
        {lengthMs > 0
          ? ticks.map((tick) => (
              <View
                key={tick}
                className={`absolute w-[3px] rounded-full ${waveform ? "inset-y-0" : "top-[10px] h-3"}`}
                style={{ left: fraction(tick) * width - 1.5, backgroundColor: colors.tick }}
              />
            ))
          : null}
        {dragMs !== null ? (
          <View className="absolute bottom-full mb-2 items-center" style={{ left: played - 100, width: 200 }}>
            {framed ?? (
              <View className="rounded-xs bg-on-surface px-1.5 py-0.5">
                <Text variant="label-md" className="text-surface" style={{ fontVariant: ["tabular-nums"] }}>
                  {formatMs(dragMs)}
                </Text>
              </View>
            )}
          </View>
        ) : null}
      </View>
    </View>
  );
}

/** The peaks as a pale outline and the loudness as a solid core around the middle line, one colour. */
function WaveShape({ waveform, lengthMs, width, color }: { waveform: Waveform; lengthMs: number; width: number; color: string }) {
  // A column every 2 px is as fine as the eye reads it, and keeps the paths small.
  const count = Math.max(1, Math.round(width / 2));
  const paths = useMemo(
    () => wavePaths(waveColumns(waveform, count, lengthMs ? lengthMs / 1000 : undefined)),
    [waveform, count, lengthMs],
  );
  if (!width) return null;
  return (
    <Svg width={width} height={waveHeight} viewBox={`0 -1 ${count} 2`} preserveAspectRatio="none">
      <Path d={paths.peaks} fill={color} opacity={0.4} />
      <Path d={paths.rms} fill={color} />
    </Svg>
  );
}
