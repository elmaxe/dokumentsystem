import type { ConfigPlugin } from "expo/config-plugins";

/** Points Android's splash icon at an animated vector drawable (withAnimatedSplash.js). */
export declare const withAnimatedSplash: ConfigPlugin<{ drawable: string; duration: number }>;
