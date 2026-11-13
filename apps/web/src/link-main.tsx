import { createI18n, pickLanguage } from "@dokument/i18n";
import { SnackbarProvider } from "@dokument/ui";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { createRootRoute, createRoute, createRouter, Outlet, RouterProvider } from "@tanstack/react-router";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { I18nextProvider } from "react-i18next";
import { LinkPage } from "./links/LinkPage";
import { validateLinkSearch } from "./links/search";
import "./index.css";

// The links gateway's page (docs/design.md §9.1): only /s/$token, with no sign-in and no calls outside the link API.
const language = pickLanguage(navigator.languages);
document.documentElement.lang = language;
const i18n = await createI18n(language);
const queryClient = new QueryClient();

const rootRoute = createRootRoute({ component: Outlet });
const linkRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/s/$token",
  validateSearch: validateLinkSearch,
  component: function LinkRoute() {
    const { token } = linkRoute.useParams();
    const { folder, file } = linkRoute.useSearch();
    return <LinkPage key={token} token={token} folder={folder} file={file} standalone />;
  },
});
const router = createRouter({ routeTree: rootRoute.addChildren([linkRoute]) });

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
