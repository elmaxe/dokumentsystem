import { useVideoPlayer, VideoView } from "expo-video";
import { View } from "react-native";
import { authHeaders } from "../state/api";
import { Icon } from "../ui/Icon";

/**
 * Video and audio with the system's player controls, from the server's preview: the original, or the playable copy
 * when the original needs one (docs/design.md §14). Audio shows a note behind the controls.
 */
export function MediaViewer({ uri, audio }: { uri: string; audio: boolean }) {
  const player = useVideoPlayer({ uri, headers: authHeaders() }, (player) => {
    player.play();
  });
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
    </View>
  );
}
