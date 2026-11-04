import preset from "@dokument/tokens/tailwind-preset";
import nativewind from "nativewind/preset";
import type { Config } from "tailwindcss";

/** NativeWind's theme: the shared tokens (docs/design.md §16.2), whose colours are CSS variables set per theme. */
export default {
  content: ["./app/**/*.{ts,tsx}", "./src/**/*.{ts,tsx}"],
  presets: [nativewind, preset],
} satisfies Config;
