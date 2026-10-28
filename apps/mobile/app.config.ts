import type { ExpoConfig } from "expo/config";

// The app id and scheme are decision 37 in docs/design.md. The scheme is where the server's sign-in page hands the
// IdP's code back to the app (§5).
const config: ExpoConfig = {
  name: "Dokumentsystem",
  slug: "dokumentsystem",
  scheme: "dokumentsystem",
  version: "0.0.0",
  orientation: "default",
  userInterfaceStyle: "automatic",
  newArchEnabled: true,
  ios: {
    bundleIdentifier: "io.github.elmaxe.dokumentsystem",
    supportsTablet: true,
    infoPlist: {
      // The local address is often plain http on a home network (§15.3).
      NSAppTransportSecurity: { NSAllowsLocalNetworking: true },
      NSLocalNetworkUsageDescription: "Connects to your Dokumentsystem server at its local address.",
    },
  },
  android: {
    package: "io.github.elmaxe.dokumentsystem",
    edgeToEdgeEnabled: true,
  },
  web: { bundler: "metro", output: "single" },
  plugins: [
    "expo-router",
    "expo-secure-store",
    "expo-localization",
    "expo-web-browser",
    // The local address may be http; the main address must be https outside development (§15.3).
    ["expo-build-properties", { android: { usesCleartextTraffic: true } }],
  ],
  experiments: { typedRoutes: true },
};

export default config;
