import type { NodeDto } from "@dokument/api";
import { formatDuration, seekFrame, stemOf } from "@dokument/core";
import { useQuery } from "@tanstack/react-query";
import { Image } from "expo-image";
import { useVideoPlayer, VideoView, type VideoPlayer } from "expo-video";
import { useEffect, useRef, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Pressable, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { authHeaders } from "../state/api";
import { useFormat } from "../state/format";
import {
  lineAt,
  seekSheetOf,
  speedLabel,
  speeds,
  transcriptQuery,
  usePlayback,
  waveformQuery,
} from "../state/media";
import { durationOf, thumbnailSource } from "../state/queries";
import { IconButton } from "../ui/Button";
import { Icon, type IconName } from "../ui/Icon";
import { Text } from "../ui/Text";
import { MatchBar } from "./MatchBar";
import { formatMs, Timeline } from "./Timeline";

/** Opened from a hit in spoken words: the moments the words are said, in milliseconds (docs/design.md §11.1). */
export type MediaSearch = { query: string; times: number[]; start?: number; onClear: () => void };

/** How long the video's controls stay while it plays and nobody touches it. */
const hideAfterMs = 3_000;

/** The largest seek-preview frame on a phone, as on the web's touch screens (§14). */
const frameBox = { width: 144, height: 128 };

/**
 * Video and audio from the server's preview (the original, or the playable copy when the original needs one) with the
 * app's own controls over the system player (docs/design.md §14, §15.3): the system's can't show our timeline. Audio
 * has its waveform as the timeline; dragging a video's timeline shows the frame from that moment.
 */
export function MediaViewer({
  node,
  uri,
  audio,
  search,
}: {
  node: NodeDto;
  uri: string;
  audio: boolean;
  search?: MediaSearch | null;
}) {
  const { t } = useTranslation();
  const times = [...(search?.times ?? [])].sort((a, b) => a - b);
  const first = search?.start ?? times[0];
  // The matches can arrive after the player: until a step is taken, the current match is the one it starts at.
  const [picked, setPicked] = useState<number | null>(null);
  const index =
    picked ?? Math.max(0, first === undefined ? 0 : times.findIndex((time) => time >= first));
  const player = useVideoPlayer(
    { uri, headers: authHeaders(), metadata: { title: stemOf(node.name), artist: node.ownerName } },
    (player) => {
      player.timeUpdateEventInterval = 0.25;
      player.staysActiveInBackground = audio;
      player.showNowPlayingNotification = audio;
      // Opened from search: plays from where the words are said.
      if (first !== undefined) player.currentTime = first / 1000;
      player.play();
    },
  );
  const playback = usePlayback(player);
  // Opened from search: goes to where the words are said once the matches are known, once.
  const startedAt = useRef<number | undefined>(first);
  useEffect(() => {
    if (first === undefined || startedAt.current !== undefined) return;
    startedAt.current = first;
    seekTo(player, first);
  }, [first, player]);
  const transcript = useQuery({ ...transcriptQuery(node.id), enabled: !!node.media?.hasAudio });
  const hasCaptions = transcript.data?.state === "ready" && transcript.data.lines.length > 0;
  const [captions, setCaptions] = useState(false);
  const line = captions && hasCaptions ? lineAt(transcript.data, playback.timeMs) : null;

  const controls: Controls = {
    player,
    playback,
    seek: (ms) => seekTo(player, ms),
    toggle: () => togglePlay(player, playback),
    skip: (seconds) => seekTo(player, (player.currentTime + seconds) * 1000),
    nextSpeed: () => nextSpeed(player, playback.rate),
    captions: hasCaptions ? { on: captions, toggle: () => setCaptions((on) => !on) } : undefined,
    line,
    ticks: times,
  };

  const step = (next: number) => () => {
    setPicked(next);
    seekTo(player, times[next]!);
    player.play();
  };

  return (
    <View className={`flex-1 ${audio ? "bg-surface" : "bg-black"}`}>
      {audio ? (
        <AudioPlayer node={node} controls={controls} />
      ) : (
        <VideoPlayerView node={node} controls={controls} />
      )}
      {search && times.length > 0 ? (
        <MatchBar
          query={search.query}
          detail={t("mobile.viewer.matchAt", {
            time: formatDuration(times[index]! / 1000),
            current: index + 1,
            total: times.length,
          })}
          previous={{
            label: t("viewer.previousSpokenMatch"),
            onPress: index > 0 ? step(index - 1) : undefined,
          }}
          next={{
            label: t("viewer.nextSpokenMatch"),
            onPress: index < times.length - 1 ? step(index + 1) : undefined,
          }}
          onClose={search.onClear}
          top
        />
      ) : null}
    </View>
  );
}

// The player is changed outside the component, where React's rules see a plain object rather than a hook's value.
function seekTo(player: VideoPlayer, ms: number) {
  const length = player.duration > 0 ? player.duration : Infinity;
  player.currentTime = Math.min(length, Math.max(0, ms / 1000));
}

function togglePlay(
  player: VideoPlayer,
  playback: { playing: boolean; timeMs: number; lengthMs: number },
) {
  if (playback.playing) return player.pause();
  // At the end, play starts it again.
  if (playback.lengthMs && playback.timeMs >= playback.lengthMs - 250) player.currentTime = 0;
  player.play();
}

function nextSpeed(player: VideoPlayer, rate: number) {
  const at = speeds.indexOf(rate as (typeof speeds)[number]);
  player.playbackRate = speeds[(at + 1) % speeds.length]!;
}

type Controls = {
  player: VideoPlayer;
  playback: ReturnType<typeof usePlayback>;
  seek: (ms: number) => void;
  toggle: () => void;
  skip: (seconds: number) => void;
  nextSpeed: () => void;
  /** Only where there's a transcript to show. */
  captions?: { on: boolean; toggle: () => void };
  /** The spoken line now, while captions are on. */
  line: string | null;
  ticks: number[];
};

/** Cover art or a placeholder, the name, and the waveform as the timeline over the controls. */
function AudioPlayer({ node, controls }: { node: NodeDto; controls: Controls }) {
  const { t } = useTranslation();
  const format = useFormat();
  const insets = useSafeAreaInsets();
  const { playback } = controls;
  const waveform = useQuery(waveformQuery(node)).data;
  const cover = node.media?.hasCover ? thumbnailSource(node, "medium") : null;
  const extension = node.name.includes(".") ? node.name.split(".").pop()!.toUpperCase() : null;
  const meta = [durationOf(node), extension, node.size != null ? format.size(node.size) : null]
    .filter(Boolean)
    .join(" · ");

  return (
    <View className="flex-1 justify-center px-4" style={{ paddingBottom: insets.bottom + 16 }}>
      {/* The system player, out of sight: some platforms play only through a mounted view. */}
      <VideoView
        player={controls.player}
        nativeControls={false}
        style={{ position: "absolute", width: 1, height: 1, opacity: 0 }}
      />
      <View className="items-center gap-1">
        <View className="mb-6 h-56 w-56 items-center justify-center overflow-hidden rounded-xl bg-surface-container-high">
          {cover ? (
            <Image
              source={cover}
              style={{ width: "100%", height: "100%" }}
              contentFit="cover"
              cachePolicy="disk"
            />
          ) : (
            <Icon name="graphic_eq" size={96} color="primary" />
          )}
        </View>
        <Text variant="title-lg" numberOfLines={2} className="text-center text-on-surface">
          {stemOf(node.name)}
        </Text>
        <Text variant="body-sm" className="text-on-surface-variant">
          {meta}
        </Text>
      </View>
      <View className="mt-4 min-h-12 justify-center" accessibilityLiveRegion="polite">
        {controls.line ? (
          <Text variant="body-lg" numberOfLines={3} className="text-center text-on-surface">
            {controls.line}
          </Text>
        ) : null}
      </View>
      <View className="mt-4">
        <Timeline
          lengthMs={playback.lengthMs}
          timeMs={playback.timeMs}
          onSeek={controls.seek}
          waveform={waveform}
          ticks={controls.ticks}
        />
        <View className="mt-1 flex-row justify-between">
          <TimeText ms={playback.timeMs} />
          <TimeText ms={playback.lengthMs} />
        </View>
      </View>
      <View className="mt-6 flex-row items-center justify-between">
        <SpeedButton rate={playback.rate} onPress={controls.nextSpeed} />
        <IconButton
          icon="replay_10"
          label={t("player.back")}
          color="on-surface"
          onPress={() => controls.skip(-10)}
        />
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={playback.playing ? t("player.pause") : t("player.play")}
          onPress={controls.toggle}
          className="h-[72px] w-[72px] items-center justify-center rounded-full bg-primary active:opacity-80"
        >
          <Icon
            name={playback.playing ? "pause" : "play_arrow"}
            filled
            size={36}
            color="on-primary"
          />
        </Pressable>
        <IconButton
          icon="forward_10"
          label={t("player.forward")}
          color="on-surface"
          onPress={() => controls.skip(10)}
        />
        {controls.captions ? (
          <IconButton
            icon={controls.captions.on ? "subtitles" : "subtitles_off"}
            label={t("player.captions")}
            color={controls.captions.on ? "primary" : "on-surface"}
            onPress={controls.captions.toggle}
          />
        ) : (
          <View className="h-touch w-touch" />
        )}
      </View>
    </View>
  );
}

/**
 * The video full width with the app's controls over it: tap shows or hides them, and they hide after a few seconds of
 * playing. Dragging the timeline shows the frame from that moment once the frame sheet has loaded, which it does the
 * first time the timeline is touched.
 */
function VideoPlayerView({ node, controls }: { node: NodeDto; controls: Controls }) {
  const { t } = useTranslation();
  const insets = useSafeAreaInsets();
  const view = useRef<VideoView>(null);
  const { playback } = controls;
  const [shown, setShown] = useState(true);
  const [scrubbing, setScrubbing] = useState(false);
  const sheet = seekSheetOf(node);
  const [wantSheet, setWantSheet] = useState(false);
  const [sheetReady, setSheetReady] = useState(false);

  // Hides the controls while it plays untouched.
  useEffect(() => {
    if (!shown || !playback.playing || scrubbing) return;
    const timer = setTimeout(() => setShown(false), hideAfterMs);
    return () => clearTimeout(timer);
  }, [shown, playback.playing, scrubbing, playback.rate]);

  const frame = (ms: number): ReactNode | null => {
    if (!sheet || !sheetReady) return null;
    const cut = seekFrame(sheet, ms, frameBox.width, frameBox.height);
    return (
      <View className="items-center gap-1">
        <View
          className="overflow-hidden rounded-sm border-2 border-white bg-black"
          style={{ width: cut.width + 4, height: cut.height + 4 }}
        >
          <Image
            source={sheet.source}
            cachePolicy="disk"
            style={{
              position: "absolute",
              left: cut.x,
              top: cut.y,
              width: cut.sheetWidth,
              height: cut.sheetHeight,
            }}
          />
        </View>
        <View className="rounded-xs bg-black/70 px-1.5 py-0.5">
          <Text variant="label-md" className="text-white" style={{ fontVariant: ["tabular-nums"] }}>
            {formatMs(ms)}
          </Text>
        </View>
      </View>
    );
  };

  return (
    <View className="flex-1">
      <VideoView
        ref={view}
        player={controls.player}
        style={{ flex: 1 }}
        contentFit="contain"
        nativeControls={false}
        fullscreenOptions={{ enable: true }}
        allowsPictureInPicture
      />
      <Pressable
        accessible={false}
        onPress={() => setShown((on) => !on)}
        className="absolute inset-0"
      />
      {controls.line ? (
        <View
          pointerEvents="none"
          className="absolute inset-x-4 items-center"
          style={{ bottom: shown ? 120 + insets.bottom : 24 + insets.bottom }}
        >
          <Text
            variant="body-lg"
            className="rounded-sm bg-black/75 px-2 py-1 text-center text-white"
          >
            {controls.line}
          </Text>
        </View>
      ) : null}
      {shown ? (
        <>
          <View
            pointerEvents="box-none"
            className="absolute inset-0 flex-row items-center justify-center gap-10"
          >
            <OverlayButton
              icon="replay_10"
              label={t("player.back")}
              onPress={() => controls.skip(-10)}
            />
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={playback.playing ? t("player.pause") : t("player.play")}
              onPress={controls.toggle}
              className="h-[72px] w-[72px] items-center justify-center rounded-full bg-black/40 active:bg-black/60"
            >
              <Icon name={playback.playing ? "pause" : "play_arrow"} filled size={40} white />
            </Pressable>
            <OverlayButton
              icon="forward_10"
              label={t("player.forward")}
              onPress={() => controls.skip(10)}
            />
          </View>
          <View
            className="absolute inset-x-0 bottom-0 bg-black/50 px-4 pt-6"
            style={{ paddingBottom: insets.bottom + 4 }}
          >
            <Timeline
              overlay
              lengthMs={playback.lengthMs}
              timeMs={playback.timeMs}
              onSeek={controls.seek}
              ticks={controls.ticks}
              onScrubStart={() => {
                setScrubbing(true);
                setWantSheet(true);
              }}
              onScrubEnd={() => setScrubbing(false)}
              preview={frame}
            />
            <View className="flex-row items-center">
              <Text
                variant="label-md"
                className="flex-1 text-white"
                style={{ fontVariant: ["tabular-nums"] }}
              >
                {`${formatMs(playback.timeMs)} / ${formatMs(playback.lengthMs)}`}
              </Text>
              <SpeedButton rate={playback.rate} onPress={controls.nextSpeed} overlay />
              {controls.captions ? (
                <OverlayButton
                  icon={controls.captions.on ? "closed_caption" : "closed_caption_off"}
                  label={t("player.captions")}
                  onPress={controls.captions.toggle}
                  small
                />
              ) : null}
              <OverlayButton
                icon="fullscreen"
                label={t("player.fullscreen")}
                onPress={() => void view.current?.enterFullscreen()}
                small
              />
            </View>
          </View>
        </>
      ) : null}
      {sheet && wantSheet && !sheetReady ? (
        // Loads the frame sheet into the cache; the frames are cut from it once it's there.
        <Image
          source={sheet.source}
          cachePolicy="disk"
          style={{ position: "absolute", width: 1, height: 1, opacity: 0 }}
          onLoad={() => setSheetReady(true)}
        />
      ) : null}
    </View>
  );
}

function OverlayButton({
  icon,
  label,
  onPress,
  small,
}: {
  icon: IconName;
  label: string;
  onPress: () => void;
  small?: boolean;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={label}
      onPress={onPress}
      hitSlop={4}
      className={`${small ? "h-touch w-touch" : "h-14 w-14"} items-center justify-center rounded-full active:bg-white/20`}
    >
      <Icon name={icon} size={small ? 24 : 32} white />
    </Pressable>
  );
}

function SpeedButton({
  rate,
  onPress,
  overlay,
}: {
  rate: number;
  onPress: () => void;
  overlay?: boolean;
}) {
  const { t, i18n } = useTranslation();
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={t("player.speed", { speed: rate })}
      onPress={onPress}
      hitSlop={4}
      className={`h-touch min-w-touch items-center justify-center rounded-full px-2 ${overlay ? "active:bg-white/20" : "active:bg-surface-container-highest"}`}
    >
      <Text variant="label-lg" className={overlay ? "text-white" : "text-on-surface"}>
        {speedLabel(rate, i18n.language)}
      </Text>
    </Pressable>
  );
}

function TimeText({ ms }: { ms: number }) {
  return (
    <Text
      variant="label-md"
      className="text-on-surface-variant"
      style={{ fontVariant: ["tabular-nums"] }}
    >
      {formatMs(ms)}
    </Text>
  );
}
