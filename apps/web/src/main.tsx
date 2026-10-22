import { createI18n, pickLanguage } from "@dokument/i18n";
import { SnackbarProvider } from "@dokument/ui";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { RouterProvider } from "@tanstack/react-router";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { I18nextProvider } from "react-i18next";
import { createAppRouter } from "./router";
import "./index.css";

const language = pickLanguage(navigator.languages);
document.documentElement.lang = language;
const i18n = await createI18n(language);
const queryClient = new QueryClient();
const router = createAppRouter(queryClient);

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <I18nextProvider i18n={i18n}>
      <QueryClientProvider client={queryClient}>
        <SnackbarProvider>
          <RouterProvider router={router} />
        </SnackbarProvider>
      </QueryClientProvider>
    </I18nextProvider>
  </StrictMode>,
);
