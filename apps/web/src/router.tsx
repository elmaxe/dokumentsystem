import type { QueryClient } from "@tanstack/react-query";
import { createRootRouteWithContext, createRoute, createRouter, Outlet, redirect } from "@tanstack/react-router";
import { AdminPeoplePage, AdminSettingsPage, AdminStoragePage } from "./admin/AdminPage";
import { sessionQuery } from "./api";
import { AppShell } from "./AppShell";
import { FilesPage } from "./files/FilesPage";
import { TrashPage } from "./files/TrashPage";
import { ViewerPage } from "./files/ViewerPage";
import { PlacePage } from "./PlacePage";
import { SettingsPage } from "./settings/SettingsPage";
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
    return <FilesPage source={{ place: "folder", folderId: null }} />;
  },
});

const folderRoute = createRoute({
  getParentRoute: () => authenticatedRoute,
  path: "/folders/$folderId",
  component: function FolderRoute() {
    const { folderId } = folderRoute.useParams();
    return <FilesPage key={folderId} source={{ place: "folder", folderId }} />;
  },
});

/** The viewer sits inside the app shell, so uploads keep running and the tray stays put while a file is open. */
export type FileSearch = { from?: "starred" | "recent" };

const fileRoute = createRoute({
  getParentRoute: () => authenticatedRoute,
  path: "/files/$fileId",
  validateSearch: (search: Record<string, unknown>): FileSearch => ({
    from: search.from === "starred" || search.from === "recent" ? search.from : undefined,
  }),
  component: function FileRoute() {
    const { fileId } = fileRoute.useParams();
    const { from } = fileRoute.useSearch();
    return <ViewerPage fileId={fileId} from={from} />;
  },
});

const trashRoute = createRoute({ getParentRoute: () => authenticatedRoute, path: "/trash", component: TrashPage });

const settingsRoute = createRoute({ getParentRoute: () => authenticatedRoute, path: "/settings", component: SettingsPage });

const sharedRoute = createRoute({
  getParentRoute: () => authenticatedRoute,
  path: "/shared",
  component: () => <PlacePage place="shared" />,
});

const placeRoutes = (["recent", "starred"] as const).map((place) =>
  createRoute({
    getParentRoute: () => authenticatedRoute,
    path: `/${place}`,
    component: () => <FilesPage key={place} source={{ place }} />,
  }),
);

/** Admin pages: people who aren't admins are sent home. */
const adminRoute = createRoute({
  getParentRoute: () => authenticatedRoute,
  path: "/admin",
  beforeLoad: ({ context }) => {
    if (context.session.user.role !== "admin") {
      throw redirect({ to: "/" });
    }
  },
  component: Outlet,
});

const adminIndexRoute = createRoute({
  getParentRoute: () => adminRoute,
  path: "/",
  beforeLoad: () => {
    throw redirect({ to: "/admin/people" });
  },
});

const adminPeopleRoute = createRoute({ getParentRoute: () => adminRoute, path: "/people", component: AdminPeoplePage });
const adminStorageRoute = createRoute({ getParentRoute: () => adminRoute, path: "/storage", component: AdminStoragePage });
const adminSettingsRoute = createRoute({ getParentRoute: () => adminRoute, path: "/settings", component: AdminSettingsPage });

/** `reason: "ended"`: this session was ended from another device (§15.4). */
export type SignInSearch = { returnTo?: string; error?: string; reason?: "ended" };

const signInRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/signin",
  validateSearch: (search: Record<string, unknown>): SignInSearch => ({
    returnTo: typeof search.returnTo === "string" ? search.returnTo : undefined,
    error: typeof search.error === "string" ? search.error : undefined,
    reason: search.reason === "ended" ? "ended" : undefined,
  }),
  component: function SignInRoute() {
    return <SignInPage {...signInRoute.useSearch()} />;
  },
});

export function createAppRouter(queryClient: QueryClient) {
  return createRouter({
    routeTree: rootRoute.addChildren([authenticatedRoute.addChildren([homeRoute, folderRoute, fileRoute, adminRoute.addChildren([adminIndexRoute, adminPeopleRoute, adminStorageRoute, adminSettingsRoute]), trashRoute, settingsRoute, sharedRoute, ...placeRoutes]), signInRoute]),
    context: { queryClient },
  });
}

declare module "@tanstack/react-router" {
  interface Register {
    router: ReturnType<typeof createAppRouter>;
  }
}
