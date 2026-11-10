import type { ExpoConfig } from "expo/config";
import path from "node:path";
import tokens from "../../docs/design-system/tokens.json";
import { withAnimatedSplash } from "./plugins/withAnimatedSplash.js";

// The app id and scheme are decision 37 in docs/design.md. The scheme is where the server's sign-in page hands the
// IdP's code back to the app (§5).
const albertSans = (face: string) =>
  path.relative(
    __dirname,
    require.resolve(`@expo-google-fonts/albert-sans/${face}/AlbertSans_${face}.ttf`),
  );
/** The splash screen's background is the app's, so the first screen follows on without a flash. */
const surface = tokens.color.tokens.find((token) => token.name === "surface")!.value as {
  light: string;
  dark: string;
};

const icons = [
  "assets/fonts/MaterialSymbolsRounded.ttf",
  "assets/fonts/MaterialSymbolsRoundedFilled.ttf",
];

const config: ExpoConfig = {
  name: "Dokumentsystem",
  slug: "dokumentsystem",
  scheme: "dokumentsystem",
  version: "0.0.0",
  orientation: "default",
  userInterfaceStyle: "automatic",
  ios: {
    bundleIdentifier: "io.github.elmaxe.dokumentsystem",
    supportsTablet: true,
    infoPlist: {
      // The local address is often plain http on a home network (§15.3).
      NSAppTransportSecurity: { NSAllowsLocalNetworking: true },
      NSLocalNetworkUsageDescription:
        "Connects to your Dokumentsystem server at its local address.",
    },
  },
  android: {
    package: "io.github.elmaxe.dokumentsystem",
  },
  web: { bundler: "metro", output: "single" },
  plugins: [
    "expo-router",
    // Built into the app rather than loaded at startup (docs/design.md §16.2): Albert Sans as one family with three
    // weights, and the icon fonts, which Android names by file and iOS by the name inside (the same, see icons.py).
    [
      "expo-font",
      {
        android: {
          fonts: [
            {
              fontFamily: "AlbertSans",
              fontDefinitions: [
                { path: albertSans("400Regular"), weight: 400 },
                { path: albertSans("500Medium"), weight: 500 },
                { path: albertSans("600SemiBold"), weight: 600 },
              ],
            },
            ...icons,
          ],
        },
        ios: {
          fonts: [
            albertSans("400Regular"),
            albertSans("500Medium"),
            albertSans("600SemiBold"),
            ...icons,
          ],
        },
      },
    ],
    "expo-secure-store",
    "expo-localization",
    "expo-web-browser",
    [
      "expo-splash-screen",
      { backgroundColor: surface.light, dark: { backgroundColor: surface.dark } },
    ],
    // The local address may be http; the main address must be https outside development (§15.3).
    ["expo-build-properties", { android: { usesCleartextTraffic: true } }],
    // The document scanner (§15.3): VisionKit asks for the camera on iOS; ML Kit's scanner needs no permission.
    [
      "react-native-document-scanner-plugin",
      { cameraPermission: "Dokumentsystem uses the camera to scan documents." },
    ],
    // Uploads (§15.3): the system pickers hand over only what's picked, so the app asks for no photo library access.
    [
      "expo-image-picker",
      { photosPermission: false, cameraPermission: false, microphonePermission: false },
    ],
    // Only local notifications, for uploads that carried on after the app was closed (decision 37).
    ["expo-notifications", { enableRemoteNotifications: false }],
    // Files, photos and videos shared from another app open the share screen; text and links don't.
    [
      "expo-share-intent",
      {
        iosActivationRules: {
          NSExtensionActivationSupportsImageWithMaxCount: 50,
          NSExtensionActivationSupportsMovieWithMaxCount: 50,
          NSExtensionActivationSupportsFileWithMaxCount: 50,
        },
        iosShareExtensionName: "Dokumentsystem",
        androidIntentFilters: ["*/*"],
        androidMultiIntentFilters: ["*/*"],
      },
    ],
  ],
  experiments: { typedRoutes: true },
};

// Android's starter-kit animation, a placeholder until the app has its own mark. Its style change waits for
// expo-splash-screen's, whose mods run first.
export default withAnimatedSplash(config, {
  drawable: "assets/splash/splash_animated.xml",
  duration: 1000,
});
