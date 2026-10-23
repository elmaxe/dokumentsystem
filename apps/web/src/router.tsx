import type { QueryClient } from "@tanstack/react-query";
import { createRootRouteWithContext, createRoute, createRouter, Outlet, redirect } from "@tanstack/react-router";
import { sessionQuery } from "./api";
import { AppShell } from "./AppShell";
import { FilesPage } from "./files/FilesPage";
import { ViewerPage } from "./files/ViewerPage";
import { PlacePage } from "./PlacePage";
import { SignInPage } from "./SignInPage";

const rootRoute = createRootRouteWithContext<{ queryClient: QueryClient }>()({ component: Outlet });

/** Everything under here needs a session; without one the user is sent to sign in and brought back afterwards. */
const authenticatedRoute = createRoute({
  getParentRoute: () => rootRoute,
  id: "authenticated",
  beforeLoad: async ({ context, location }) => {
    const session = await context.queryClient.ensureQueryData(sessionQuery);
    if (!session) {
      throw redirect({ to: "/signin", search: { returnTo: location.href } });
    }
    return { session };
  },
  component: AppShell,
});

const homeRoute = createRoute({
  getParentRoute: () => authenticatedRoute,
  path: "/",
  component: function MyFilesRoute() {
    return <FilesPage folderId={null} />;
  },
});

const folderRoute = createRoute({
  getParentRoute: () => authenticatedRoute,
  path: "/folders/$folderId",
  component: function FolderRoute() {
    const { folderId } = folderRoute.useParams();
    return <FilesPage key={folderId} folderId={folderId} />;
  },
});

/** The viewer sits inside the app shell, so uploads keep running and the tray stays put while a file is open. */
const fileRoute = createRoute({
  getParentRoute: () => authenticatedRoute,
  path: "/files/$fileId",
  component: function FileRoute() {
    const { fileId } = fileRoute.useParams();
    return <ViewerPage fileId={fileId} />;
  },
});

const placeRoutes = (["shared", "recent", "starred", "trash"] as const).map((place) =>
  createRoute({
    getParentRoute: () => authenticatedRoute,
    path: `/${place}`,
    component: () => <PlacePage place={place} />,
  }),
);

export type SignInSearch = { returnTo?: string; error?: string };

const signInRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/signin",
  validateSearch: (search: Record<string, unknown>): SignInSearch => ({
    returnTo: typeof search.returnTo === "string" ? search.returnTo : undefined,
    error: typeof search.error === "string" ? search.error : undefined,
  }),
  component: function SignInRoute() {
    return <SignInPage {...signInRoute.useSearch()} />;
  },
});

export function createAppRouter(queryClient: QueryClient) {
  return createRouter({
    routeTree: rootRoute.addChildren([authenticatedRoute.addChildren([homeRoute, folderRoute, fileRoute, ...placeRoutes]), signInRoute]),
    context: { queryClient },
  });
}

declare module "@tanstack/react-router" {
  interface Register {
    router: ReturnType<typeof createAppRouter>;
  }
}
