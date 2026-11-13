import { seekPreviewsUrl, waveformUrl, type NodeDto, type Transcript } from "@dokument/api";
import { parseWaveform, type SeekSheet, type Waveform } from "@dokument/core";
import { queryOptions } from "@tanstack/react-query";
import { useEvent } from "expo";
import type { VideoPlayer } from "expo-video";
import { api, authHeaders, baseUrl, unwrap } from "./api";

/** An audio file's waveform, once the worker has drawn it (docs/design.md §14). Fails to the plain timeline. */
export const waveformQuery = (node: NodeDto) =>
  queryOptions({
    queryKey: ["waveform", node.id, node.currentVersionId],
    queryFn: async (): Promise<Waveform> => {
      const response = await fetch(waveformUrl(node.id, node.currentVersionId, baseUrl()), { headers: authHeaders() });
      if (!response.ok) throw new Error(`The waveform didn't load (${response.status}).`);
      return parseWaveform(await response.arrayBuffer());
    },
    enabled: !!node.media?.hasWaveform,
    staleTime: Infinity,
    retry: 1,
  });

/** What's said in a recording, for captions (§11.1). Asked again while it's being transcribed. */
export const transcriptQuery = (id: string) =>
  queryOptions({
    queryKey: ["node", id, "transcript"],
    queryFn: (): Promise<Transcript> =>
      unwrap(api().GET("/api/v1/nodes/{id}/transcript", { params: { path: { id } } })),
    refetchInterval: (query) => (query.state.data?.state === "running" ? 10_000 : false),
  });

/** A video's frames over the timeline, and where the sheet loads from with the session (§14). */
export function seekSheetOf(node: NodeDto): (SeekSheet & { source: { uri: string; headers: Record<string, string>; cacheKey: string } }) | null {
  const sheet = node.media?.seekPreviews;
  if (!sheet) return null;
  return {
    ...sheet,
    source: {
      uri: seekPreviewsUrl(node.id, baseUrl()),
      headers: authHeaders(),
      cacheKey: `${node.id}/${node.currentVersionId ?? ""}/seek-previews`,
    },
  };
}

/** The speeds the speed button steps through. */
export const speeds = [1, 1.25, 1.5, 2, 0.75] as const;

/** "1×", "1.25×" or "0,75×", in the person's language. */
export const speedLabel = (rate: number, language: string) =>
  `${rate.toLocaleString(language === "en" ? "en-GB" : language)}×`;

/** The player's state as the controls show it, kept up to date from its events. */
export function usePlayback(player: VideoPlayer) {
  const { isPlaying } = useEvent(player, "playingChange", { isPlaying: player.playing });
  const { currentTime } = useEvent(player, "timeUpdate", {
    currentTime: player.currentTime,
    currentLiveTimestamp: null,
    currentOffsetFromLive: null,
    bufferedPosition: 0,
  });
  const { status } = useEvent(player, "statusChange", { status: player.status });
  const { playbackRate } = useEvent(player, "playbackRateChange", { playbackRate: player.playbackRate });
  // The length is known once the source has loaded; the status change re-renders with it.
  const lengthMs = status === "readyToPlay" || player.duration > 0 ? Math.max(0, player.duration * 1000) : 0;
  return { playing: isPlaying, timeMs: currentTime * 1000, lengthMs, status, rate: playbackRate };
}

/** The spoken line at `ms`, for captions. */
export function lineAt(transcript: Transcript | undefined, ms: number) {
  return transcript?.lines.find((line) => line.startMs <= ms && ms < line.endMs)?.text ?? null;
}
