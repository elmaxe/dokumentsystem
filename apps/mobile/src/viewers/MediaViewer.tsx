import { formatDuration } from "@dokument/core";
import { useVideoPlayer, VideoView } from "expo-video";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { View } from "react-native";
import { authHeaders } from "../state/api";
import { Icon } from "../ui/Icon";
import { MatchBar } from "./MatchBar";

/** Opened from a hit in spoken words: the moments the words are said, in milliseconds (docs/design.md §11.1). */
export type MediaSearch = { query: string; times: number[]; start?: number; onClear: () => void };

/**
 * Video and audio with the system's player controls, from the server's preview: the original, or the playable copy
 * when the original needs one (docs/design.md §14). Audio shows a note behind the controls.
 */
export function MediaViewer({ uri, audio, search }: { uri: string; audio: boolean; search?: MediaSearch | null }) {
  const { t } = useTranslation();
  const times = [...(search?.times ?? [])].sort((a, b) => a - b);
  const first = search?.start ?? times[0];
  const [index, setIndex] = useState(() => Math.max(0, first === undefined ? 0 : times.findIndex((time) => time >= first)));
  const player = useVideoPlayer({ uri, headers: authHeaders() }, (player) => {
    // Opened from search: plays from where the words are said.
    if (first !== undefined) player.currentTime = first / 1000;
    player.play();
  });
  const seek = (next: number) => () => {
    setIndex(next);
    player.currentTime = times[next]! / 1000;
    player.play();
  };
  return (
    <View className="flex-1 bg-black">
      {audio ? (
        <View className="absolute inset-0 items-center justify-center">
          <Icon name="music_note" size={96} color="on-surface-variant" />
        </View>
      ) : null}
      <VideoView
        player={player}
        style={{ flex: 1 }}
        contentFit="contain"
        nativeControls
        fullscreenOptions={{ enable: !audio }}
        allowsPictureInPicture={!audio}
      />
      {search && times.length > 0 ? (
        <MatchBar
          query={search.query}
          detail={t("mobile.viewer.matchAt", {
            time: formatDuration(times[index]! / 1000),
            current: index + 1,
            total: times.length,
          })}
          previous={{ label: t("viewer.previousSpokenMatch"), onPress: index > 0 ? seek(index - 1) : undefined }}
          next={{ label: t("viewer.nextSpokenMatch"), onPress: index < times.length - 1 ? seek(index + 1) : undefined }}
          onClose={search.onClear}
          top
        />
      ) : null}
    </View>
  );
}
