import { resources } from "@dokument/i18n";
import type { Decorator, Preview } from "@storybook/react-vite";
import i18next from "i18next";
import { useEffect, type ReactNode } from "react";
import { I18nextProvider, initReactI18next } from "react-i18next";
import { SnackbarProvider } from "../src/components/Snackbar";
import "../src/styles.css";

const i18n = i18next.createInstance();
void i18n.use(initReactI18next).init({
  lng: "en",
  fallbackLng: "en",
  resources,
  initAsync: false,
  interpolation: { escapeValue: false },
});

/** Applies the toolbar's theme and language, the same way the apps do (data-theme, i18next). */
function ThemeAndLanguage({
  theme,
  locale,
  children,
}: {
  theme: string;
  locale: string;
  children: ReactNode;
}) {
  if (i18n.language !== locale) void i18n.changeLanguage(locale);
  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    document.documentElement.lang = locale;
  }, [theme, locale]);
  return (
    <I18nextProvider i18n={i18n} key={locale}>
      <SnackbarProvider>{children}</SnackbarProvider>
    </I18nextProvider>
  );
}

const withThemeAndLanguage: Decorator = (Story, context) => (
  <ThemeAndLanguage
    theme={context.globals.theme as string}
    locale={context.globals.locale as string}
  >
    <Story />
  </ThemeAndLanguage>
);

const preview: Preview = {
  decorators: [withThemeAndLanguage],
  globalTypes: {
    theme: {
      description: "Colour theme",
      toolbar: {
        title: "Theme",
        icon: "mirror",
        items: [
          { value: "light", title: "Light", icon: "sun" },
          { value: "dark", title: "Dark", icon: "moon" },
        ],
        dynamicTitle: true,
      },
    },
    locale: {
      description: "Language",
      toolbar: {
        title: "Language",
        icon: "globe",
        items: [
          { value: "en", title: "English" },
          { value: "sv", title: "Svenska" },
        ],
        dynamicTitle: true,
      },
    },
  },
  initialGlobals: {
    theme: "light",
    locale: "en",
    viewport: { value: "desktop", isRotated: false },
  },
  parameters: {
    layout: "padded",
    controls: { expanded: true },
    a11y: { test: "todo" },
    viewport: {
      // The widths from docs/design.md §16.3.
      options: {
        phone: { name: "Phone (390)", styles: { width: "390px", height: "844px" }, type: "mobile" },
        tablet: {
          name: "Small tablet (720)",
          styles: { width: "720px", height: "1024px" },
          type: "tablet",
        },
        laptop: {
          name: "Laptop (1024)",
          styles: { width: "1024px", height: "768px" },
          type: "desktop",
        },
        desktop: {
          name: "Desktop (1440)",
          styles: { width: "1440px", height: "900px" },
          type: "desktop",
        },
      },
    },
    options: {
      storySort: { order: ["Screens", "Components"] },
    },
  },
};

export default preview;
