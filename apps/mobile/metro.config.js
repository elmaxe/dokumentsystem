const { getDefaultConfig } = require("expo/metro-config");
const { withNativeWind } = require("nativewind/metro");

// Expo finds the pnpm workspace by itself, so the shared packages resolve from their sources.
module.exports = withNativeWind(getDefaultConfig(__dirname), { input: "./global.css" });
