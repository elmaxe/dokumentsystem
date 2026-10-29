import { Image } from "expo-image";
import { useState } from "react";
import { View, type LayoutRectangle } from "react-native";
import {
  GestureDetector,
  usePanGesture,
  usePinchGesture,
  useSimultaneousGestures,
  useExclusiveGestures,
  useTapGesture,
} from "react-native-gesture-handler";
import Animated, { useAnimatedStyle, useSharedValue, withTiming } from "react-native-reanimated";
import { authHeaders } from "../state/api";

const maxScale = 6;

/**
 * An image fitted to the screen. Pinch zooms around the fingers, a double tap zooms in to 2.5× or back out, and a drag
 * moves a zoomed image without letting it leave the screen.
 */
export function ImageViewer({
  uri,
  cacheKey,
  label,
}: {
  uri: string;
  cacheKey: string;
  label: string;
}) {
  const [frame, setFrame] = useState<LayoutRectangle | null>(null);
  const width = useSharedValue(0);
  const height = useSharedValue(0);
  const scale = useSharedValue(1);
  const savedScale = useSharedValue(1);
  const x = useSharedValue(0);
  const y = useSharedValue(0);
  const savedX = useSharedValue(0);
  const savedY = useSharedValue(0);

  // How far the zoomed image may move before its edge comes away from the screen's.
  const clamp = (value: number, size: number, zoom: number) => {
    "worklet";
    const limit = (size * (zoom - 1)) / 2;
    return Math.min(limit, Math.max(-limit, value));
  };

  const pinch = usePinchGesture({
    onActivate: () => {
      "worklet";
      savedScale.value = scale.value;
      savedX.value = x.value;
      savedY.value = y.value;
    },
    onUpdate: (event) => {
      "worklet";
      const next = Math.min(maxScale, Math.max(1, savedScale.value * event.scale));
      // Keep the point between the fingers where it is.
      const focusX = event.focalX - width.value / 2;
      const focusY = event.focalY - height.value / 2;
      const ratio = next / savedScale.value;
      scale.value = next;
      x.value = clamp(focusX - (focusX - savedX.value) * ratio, width.value, next);
      y.value = clamp(focusY - (focusY - savedY.value) * ratio, height.value, next);
    },
  });

  const pan = usePanGesture({
    minPointers: 1,
    maxPointers: 2,
    onActivate: () => {
      "worklet";
      savedX.value = x.value;
      savedY.value = y.value;
    },
    onUpdate: (event) => {
      "worklet";
      if (scale.value <= 1) return;
      x.value = clamp(savedX.value + event.translationX, width.value, scale.value);
      y.value = clamp(savedY.value + event.translationY, height.value, scale.value);
    },
  });

  const doubleTap = useTapGesture({
    numberOfTaps: 2,
    onActivate: (event) => {
      "worklet";
      if (scale.value > 1) {
        scale.value = withTiming(1);
        x.value = withTiming(0);
        y.value = withTiming(0);
        return;
      }
      const next = 2.5;
      const focusX = event.x - width.value / 2;
      const focusY = event.y - height.value / 2;
      scale.value = withTiming(next);
      x.value = withTiming(clamp(-focusX * (next - 1), width.value, next));
      y.value = withTiming(clamp(-focusY * (next - 1), height.value, next));
    },
  });

  const gesture = useExclusiveGestures(doubleTap, useSimultaneousGestures(pinch, pan));
  const style = useAnimatedStyle(() => ({
    transform: [{ translateX: x.value }, { translateY: y.value }, { scale: scale.value }],
  }));

  return (
    <GestureDetector gesture={gesture}>
      <View
        className="flex-1 overflow-hidden bg-black"
        onLayout={(event) => {
          const layout = event.nativeEvent.layout;
          width.value = layout.width;
          height.value = layout.height;
          setFrame(layout);
        }}
      >
        {frame ? (
          <Animated.View style={[{ width: frame.width, height: frame.height }, style]}>
            <Image
              source={{ uri, headers: authHeaders(), cacheKey }}
              style={{ width: "100%", height: "100%" }}
              contentFit="contain"
              cachePolicy="disk"
              transition={120}
              accessibilityLabel={label}
              accessible
            />
          </Animated.View>
        ) : null}
      </View>
    </GestureDetector>
  );
}
