import "../global.css";
import { PersistQueryClientProvider } from "@tanstack/react-query-persist-client";
import { useFonts } from "expo-font";
import { Stack } from "expo-router";
import * as SplashScreen from "expo-splash-screen";
import { StatusBar } from "expo-status-bar";
import * as SystemUI from "expo-system-ui";
import { useEffect } from "react";
import { Platform } from "react-native";
import { GestureHandlerRootView } from "react-native-gesture-handler";
import { SafeAreaProvider } from "react-native-safe-area-context";
import { I18nProvider } from "../src/state/i18n";
import { persistOptions, queryClient } from "../src/state/queryClient";
import { SessionProvider, useSession } from "../src/state/session";
import { ThemeProvider, useTheme } from "../src/state/theme";
import { webFonts } from "../src/ui/Text";

void SplashScreen.preventAutoHideAsync();

export default function RootLayout() {
  // The app has its fonts built in (app.config.ts); only the web build loads them.
  const [fontsLoaded] = useFonts(Platform.OS === "web" ? webFonts : {});

  return (
    <GestureHandlerRootView style={{ flex: 1 }}>
      <SafeAreaProvider>
        <PersistQueryClientProvider client={queryClient} persistOptions={persistOptions}>
          <SessionProvider>
            <I18nProvider>
              <ThemeProvider>
                <Navigation ready={fontsLoaded} />
              </ThemeProvider>
            </I18nProvider>
          </SessionProvider>
        </PersistQueryClientProvider>
      </SafeAreaProvider>
    </GestureHandlerRootView>
  );
}

function Navigation({ ready }: { ready: boolean }) {
  const { state } = useSession();
  const theme = useTheme();
  const loading = !ready || state.status === "loading";

  useEffect(() => {
    void SystemUI.setBackgroundColorAsync(theme.color("surface"));
  }, [theme]);

  useEffect(() => {
    if (!loading) void SplashScreen.hideAsync();
  }, [loading]);

  if (loading) return null;
  const signedIn = state.status === "signedIn";
  const hasServer = state.status === "signedOut" && !!state.server;

  return (
    <>
      <StatusBar style={theme.scheme === "dark" ? "light" : "dark"} />
      <Stack
        screenOptions={{
          headerShown: false,
          contentStyle: { backgroundColor: theme.color("surface") },
        }}
      >
        <Stack.Protected guard={signedIn}>
          <Stack.Screen name="(app)" />
        </Stack.Protected>
        <Stack.Protected guard={!signedIn && hasServer}>
          <Stack.Screen name="sign-in" />
        </Stack.Protected>
        <Stack.Protected guard={!signedIn && !hasServer}>
          <Stack.Screen name="welcome" />
        </Stack.Protected>
        <Stack.Screen name="signed-in" />
        <Stack.Screen name="signed-out" />
      </Stack>
    </>
  );
}
