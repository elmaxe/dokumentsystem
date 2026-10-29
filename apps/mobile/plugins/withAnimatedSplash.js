// Plain JavaScript: Expo compiles app.config.ts alone, not the files it imports.
const fs = require("node:fs");
const path = require("node:path");
const { withAndroidStyles, withDangerousMod } = require("expo/config-plugins");

/**
 * Android's splash screen with an animated vector drawable (docs/design.md §15.3). expo-splash-screen already uses the
 * platform's splash screen API, but points its icon at a PNG; this copies the AVD into the drawables and points the
 * icon at it instead. Expo still decides when the splash leaves (SplashScreen.hideAsync in app/_layout.tsx).
 *
 * @type {import("expo/config-plugins").ConfigPlugin<{ drawable: string; duration: number }>}
 */
const withAnimatedSplash = (config, { drawable, duration }) => {
  config = withDangerousMod(config, [
    "android",
    (config) => {
      const target = path.join(
        config.modRequest.platformProjectRoot,
        "app/src/main/res/drawable/splashscreen_animated.xml",
      );
      fs.mkdirSync(path.dirname(target), { recursive: true });
      fs.copyFileSync(path.join(config.modRequest.projectRoot, drawable), target);
      return config;
    },
  ]);

  return withAndroidStyles(config, (config) => {
    const splash = config.modResults.resources.style?.find(
      (style) => style.$.name === "Theme.App.SplashScreen",
    );
    if (!splash) throw new Error("withAnimatedSplash runs after expo-splash-screen's style.");
    splash.item = [
      ...splash.item.filter(
        (item) =>
          item.$.name !== "windowSplashScreenAnimatedIcon" &&
          item.$.name !== "windowSplashScreenAnimationDuration",
      ),
      { $: { name: "windowSplashScreenAnimatedIcon" }, _: "@drawable/splashscreen_animated" },
      // Read by Android 12; later versions take the length from the drawable itself.
      { $: { name: "windowSplashScreenAnimationDuration" }, _: String(duration) },
    ];
    return config;
  });
};

module.exports = { withAnimatedSplash };
