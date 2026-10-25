import { pickLanguage } from "@dokument/i18n";
import {
  AccountMenu,
  ActionMenu,
  AdminBottomNav,
  AdminRail,
  adminPlaces,
  Avatar,
  BottomNav,
  DisplaySettingsProvider,
  Icon,
  IconButton,
  NavRail,
  UploadTray,
  useSnackbar,
  type AdminPlace,
  type MenuAction,
  type Place,
} from "@dokument/ui";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Outlet, useNavigate, useParams, useRouterState } from "@tanstack/react-router";
import { useEffect, useRef, useState, type DragEvent } from "react";
import { useTranslation } from "react-i18next";
import { logout, sessionQuery } from "./api";
import { useAccountSettings } from "./settings/account";
import { NameDialog } from "./files/dialogs";
import { drive, errorMessage, folderQuery, invalidateFolders, nameKey, storageQuery } from "./files/drive";
import { UploadProvider } from "./files/uploads";
import { useUploads } from "./files/uploadsContext";
import { SearchBox } from "./search/SearchBox";
import { validateSearchParams } from "./search/search";

const placePaths: Record<Place, string> = {
  files: "/",
  shared: "/shared",
  recent: "/recent",
  starred: "/starred",
  trash: "/trash",
};

/** The frame around every signed-in page: nav rail or bottom nav, top bar and upload tray (§15.2, §16.3). */
export function AppShell() {
  return (
    <UploadProvider>
      <Shell />
    </UploadProvider>
  );
}

function Shell() {
  const { t, i18n } = useTranslation();
  const toast = useSnackbar();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const uploads = useUploads();
  const session = useQuery(sessionQuery);
  const storage = useQuery(storageQuery);
  const pathname = useRouterState({ select: (state) => state.location.pathname });
  const locationSearch = useRouterState({ select: (state) => state.location.search });
  const { folderId = null } = useParams({ strict: false }) as { folderId?: string };
  const place = (Object.entries(placePaths).find(([, path]) => path !== "/" && pathname.startsWith(path))?.[0] ??
    "files") as Place;
  const inAdmin = pathname.startsWith("/admin");
  const adminPlace = adminPlaces.find((item) => pathname.startsWith(`/admin/${item.id}`))?.id;
  const toAdmin = (id: AdminPlace) => void navigate({ to: `/admin/${id}` });
  const exitAdmin = () => void navigate({ to: "/" });
  const inSettings = pathname.startsWith("/settings");
  const inSearch = pathname === "/search";
  const inFiles = place === "files" && !inAdmin && !inSettings && !inSearch;
  const listing = useQuery({ ...folderQuery(inFiles ? folderId : null) });
  const folderName = listing.data?.folder?.name ?? t("nav.myFiles");
  const [trayCollapsed, setTrayCollapsed] = useState(false);
  const [dropping, setDropping] = useState(false);
  const [creating, setCreating] = useState(false);
  const fileInput = useRef<HTMLInputElement>(null);
  const target = inFiles ? folderId : null;

  const newActions: MenuAction[] = [
    { id: "folder", icon: "create_new_folder", label: t("files.newFolder") },
    { id: "upload", icon: "upload_file", label: t("files.uploadFiles"), separatorBefore: true },
  ];
  const onNew = (id: string) => (id === "upload" ? fileInput.current?.click() : setCreating(true));

  const pick = (files: FileList | null) => {
    if (!files || files.length === 0) return;
    uploads.start(Array.from(files), {
      folderId: target,
      folderName: inFiles ? folderName : t("nav.myFiles"),
      existingNames: new Set((inFiles ? (listing.data?.items ?? []) : []).map((item) => nameKey(item.name))),
    });
    setTrayCollapsed(false);
  };

  const dragProps = inFiles
    ? {
        onDragOver: (event: DragEvent) => {
          if (!event.dataTransfer.types.includes("Files")) return;
          event.preventDefault();
          setDropping(true);
        },
        onDragLeave: (event: DragEvent) => {
          if (!event.currentTarget.contains(event.relatedTarget as globalThis.Node | null)) setDropping(false);
        },
        onDrop: (event: DragEvent) => {
          event.preventDefault();
          setDropping(false);
          pick(event.dataTransfer.files);
        },
      }
    : {};

  async function signOut() {
    await logout();
    queryClient.setQueryData(sessionQuery.queryKey, null);
    await navigate({ to: "/signin" });
  }

  const name = session.data?.user.displayName ?? "";
  const account = useAccountSettings();
  const picked = account.language;

  // The saved language wins over the browser's (§23); "Same as browser" clears it.
  useEffect(() => {
    if (session.data === undefined) return;
    const language = picked ?? pickLanguage(navigator.languages);
    document.documentElement.lang = language;
    if (i18n.language !== language) void i18n.changeLanguage(language);
  }, [session.data, picked, i18n]);

  return (
    <DisplaySettingsProvider value={{ showSeconds: account.showSeconds }}>
    <div className="flex h-dvh min-h-0 bg-surface text-on-surface">
      {inAdmin ? (
        <AdminRail active={adminPlace} onNavigate={toAdmin} onExit={exitAdmin} className="max-md:hidden" />
      ) : (
        <NavRail
          active={inSettings || inSearch ? undefined : place}
          onNavigate={(id) => void navigate({ to: placePaths[id] })}
          newActions={newActions}
          onNewAction={onNew}
          used={storage.data?.usedBytes ?? 0}
          quota={storage.data ? (storage.data.quotaBytes ?? null) : null}
          trash={storage.data?.trashBytes}
          className="max-md:hidden"
        />
      )}
      <div className="flex min-w-0 flex-1 flex-col">
        <header className="flex h-topbar shrink-0 items-center gap-2 px-4 sm:px-6">
          {inAdmin && (
            <IconButton icon="arrow_back" label={t("admin.nav.exit")} onClick={exitAdmin} className="-ml-2 md:hidden" />
          )}
          {!inAdmin && !inSettings && (
            <SearchBox
              params={inSearch ? validateSearchParams(locationSearch as Record<string, unknown>) : undefined}
              folderId={inFiles ? folderId : null}
              onBack={inSearch ? () => void navigate({ to: "/" }) : undefined}
              className="max-w-180 min-w-0 flex-1"
            />
          )}
          <AccountMenu
            name={name}
            email={session.data?.user.email}
            language={picked ?? "browser"}
            currentLanguage={i18n.language === "sv" ? "sv" : "en"}
            onLanguageChange={(choice) => void account.setLanguage(choice)}
            onSettings={() => void navigate({ to: "/settings" })}
            onTrash={() => void navigate({ to: "/trash" })}
            onAdmin={session.data?.user.role === "admin" ? () => void navigate({ to: "/admin/people" }) : undefined}
            onSignOut={() => void signOut()}
            trigger={
              <button
                type="button"
                aria-label={t("account.menu")}
                title={name}
                className="ml-auto inline-flex size-12 shrink-0 cursor-pointer items-center justify-center rounded-full hover:bg-on-surface/8"
              >
                <Avatar name={name} tone={1} />
              </button>
            }
          />
        </header>
        <div {...dragProps} className="relative flex min-h-0 flex-1">
          <Outlet />
          {dropping && (
            <div className="pointer-events-none absolute inset-2 z-10 flex flex-col items-center justify-center gap-3 rounded-lg border-2 border-dashed border-primary bg-primary-container/90 text-center text-on-primary-container">
              <Icon name="upload" filled size={48} />
              <p className="m-0 text-headline-sm">{t("files.empty.dropTitle", { folder: folderName })}</p>
              <p className="m-0">{t("files.empty.dropText")}</p>
            </div>
          )}
        </div>
        {inAdmin ? (
          <AdminBottomNav active={adminPlace} onNavigate={toAdmin} className="shrink-0 md:hidden" />
        ) : (
          <BottomNav
            active={
              inSearch ? "search" : !inSettings && (place === "files" || place === "shared" || place === "recent") ? place : undefined
            }
            onNavigate={(id) => {
              if (id !== "search") return void navigate({ to: placePaths[id] });
              void navigate({ to: "/search" }).then(() => document.querySelector<HTMLInputElement>("header input[type=search]")?.focus());
            }}
            className="shrink-0 md:hidden"
          />
        )}
      </div>

      {inFiles && uploads.items.length === 0 && (
        <ActionMenu
          actions={newActions}
          onAction={onNew}
          trigger={
            <button
              type="button"
              aria-label={t("files.new")}
              className="fixed right-4 bottom-24 z-20 inline-flex size-14 cursor-pointer items-center justify-center rounded-md bg-primary-container text-on-primary-container shadow-elevation-2 md:hidden"
            >
              <Icon name="add" />
            </button>
          }
        />
      )}

      {uploads.items.length > 0 && (
        <UploadTray
          items={uploads.items}
          collapsed={trayCollapsed}
          onCollapsedChange={setTrayCollapsed}
          onClose={uploads.clear}
          onPause={uploads.pause}
          onResume={uploads.resume}
          onRetry={uploads.retry}
          onCancel={uploads.cancel}
          onShowInFolder={(id) => {
            const result = uploads.resultOf(id);
            setTrayCollapsed(true);
            void (result?.folderId
              ? navigate({ to: "/folders/$folderId", params: { folderId: result.folderId } })
              : navigate({ to: "/" }));
          }}
          className="fixed inset-x-2 bottom-22 z-20 w-auto md:inset-x-auto md:right-6 md:bottom-6 md:w-95"
        />
      )}

      <NameDialog
        key={String(creating)}
        open={creating}
        onOpenChange={setCreating}
        title={t("dialogs.newFolder.title")}
        label={t("dialogs.newFolder.label")}
        submitLabel={t("dialogs.newFolder.create")}
        initial={t("files.newFolder")}
        onSubmit={async (value) => {
          try {
            await drive.createFolder(target, value);
            await invalidateFolders(queryClient, [target]);
            toast(t("toasts.folderCreated"));
            return null;
          } catch (error) {
            return errorMessage(t, error);
          }
        }}
      />

      <input
        ref={fileInput}
        type="file"
        multiple
        hidden
        onChange={(event) => {
          pick(event.target.files);
          event.target.value = "";
        }}
      />
    </div>
    </DisplaySettingsProvider>
  );
}
