import AsyncStorage from "@react-native-async-storage/async-storage";
import { tokens, themeVars, type ColorToken } from "@dokument/tokens";
import { vars } from "nativewind";
import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { useColorScheme, View } from "react-native";

/** Same as device, Light or Dark (decision 36). The app keeps its own choice on the phone, like each browser does. */
export type ThemePreference = "system" | "light" | "dark";
export type Scheme = "light" | "dark";

const storageKey = "dokument.theme";
const schemeVars = { light: vars(themeVars.light), dark: vars(themeVars.dark) };

type ThemeContext = {
  preference: ThemePreference;
  setPreference: (preference: ThemePreference) => void;
  scheme: Scheme;
  /** A token's colour in the current theme, for props that don't take class names (icons, the status bar). */
  color: (token: ColorToken) => string;
};

const Context = createContext<ThemeContext | null>(null);

export function ThemeProvider({ children }: { children: ReactNode }) {
  const device = useColorScheme();
  const [preference, setPreferenceState] = useState<ThemePreference>("system");

  useEffect(() => {
    AsyncStorage.getItem(storageKey)
      .then((stored) => {
        if (stored === "light" || stored === "dark") setPreferenceState(stored);
      })
      .catch(() => {});
  }, []);

  const scheme: Scheme = preference === "system" ? (device === "dark" ? "dark" : "light") : preference;
  const value = useMemo<ThemeContext>(
    () => ({
      preference,
      setPreference: (next) => {
        setPreferenceState(next);
        void (next === "system" ? AsyncStorage.removeItem(storageKey) : AsyncStorage.setItem(storageKey, next));
      },
      scheme,
      color: (token) => tokens.color[token][scheme],
    }),
    [preference, scheme],
  );

  return (
    <Context.Provider value={value}>
      <View style={[{ flex: 1 }, schemeVars[scheme]]}>{children}</View>
    </Context.Provider>
  );
}

export function useTheme(): ThemeContext {
  const context = useContext(Context);
  if (!context) throw new Error("useTheme needs a ThemeProvider");
  return context;
}
