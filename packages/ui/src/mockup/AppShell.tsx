import { useRef, useState, type DragEvent } from "react";
import { useTranslation } from "react-i18next";
import { Avatar } from "../components/Avatar";
import { BottomNav, type BottomPlace } from "../components/BottomNav";
import { DetailsPanel } from "../components/DetailsPanel";
import { Icon } from "../components/Icon";
import { ActionMenu, type MenuAction } from "../components/Menu";
import { NavRail } from "../components/NavRail";
import { SearchBar } from "../components/SearchBar";
import { useSnackbar } from "../lib/snackbar";
import { UploadTray } from "../components/UploadTray";
import { cn } from "../lib/cn";
import { AdminScreen } from "./AdminScreen";
import { details as detailsFor, quota } from "./data";
import { FilesScreen } from "./FilesScreen";
import { SearchScreen } from "./SearchScreen";
import { folderPath, useMockup } from "./store";
import { ViewerScreen } from "./ViewerScreen";

/** The frame around every screen: nav rail or bottom nav, top bar, details panel and upload tray (§15.2, §16.3). */
export function AppShell() {
  const { t } = useTranslation();
  const toast = useSnackbar();
  const app = useMockup();
  const { route } = app;
  const [query, setQuery] = useState(route.screen === "search" ? route.query : "");
  const [trayCollapsed, setTrayCollapsed] = useState(false);
  const [dropping, setDropping] = useState(false);
  const fileInput = useRef<HTMLInputElement>(null);

  const newActions: MenuAction[] = [
    { id: "folder", icon: "create_new_folder", label: t("files.newFolder") },
    { id: "upload", icon: "upload_file", label: t("files.uploadFiles"), separatorBefore: true },
  ];
  const onNew = (id: string) => {
    if (id === "upload") fileInput.current?.click();
    else {
      const folderId = route.screen === "files" ? route.folderId : null;
      const name = t("files.newFolder");
      app.setNodes((nodes) => [
        ...nodes,
        {
          id: `folder-${Date.now()}`,
          parentId: folderId,
          name,
          kind: "folder",
          owner: "Axel",
          modified: new Date().toISOString(),
        },
      ]);
      toast(t("toasts.folderCreated"));
    }
  };

  const pick = (files: FileList | null) => {
    if (!files || files.length === 0) return;
    app.startUpload(
      Array.from(files).map((file) => ({
        name: file.name,
        size: file.size,
        url: file.type.startsWith("image/") ? URL.createObjectURL(file) : undefined,
      })),
    );
    setTrayCollapsed(false);
  };

  const canDrop = route.screen === "files" && route.place === "files";
  const dragProps = canDrop
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

  if (route.screen === "viewer") return <ViewerScreen />;

  const place = route.screen === "files" ? route.place : undefined;
  const bottom: BottomPlace | undefined =
    route.screen === "search" ? "search" : place === "files" || place === "shared" || place === "recent" ? place : undefined;
  const single = app.selected.length === 1 ? app.nodes.find((n) => n.id === app.selected[0]) : undefined;
  const showDetails = route.screen === "files" && app.details.open && single;
  const folderName =
    route.screen === "files" && route.folderId
      ? (app.nodes.find((n) => n.id === route.folderId)?.name ?? "")
      : t("nav.myFiles");

  return (
    <div className="flex h-dvh min-h-0 bg-surface text-on-surface">
      <NavRail
        active={place}
        onNavigate={(id) => app.navigate({ screen: "files", place: id, folderId: null })}
        newActions={newActions}
        onNewAction={onNew}
        used={quota.used}
        quota={quota.quota}
        className="max-md:hidden"
      />
      <div className="flex min-w-0 flex-1 flex-col">
        <header className="flex h-topbar shrink-0 items-center gap-2 px-4 sm:px-6">
          <SearchBar
            value={query}
            onChange={setQuery}
            onSubmit={(value) => app.navigate({ screen: "search", query: value })}
            onBack={route.screen === "search" ? () => app.navigate({ screen: "files", place: "files", folderId: null }) : undefined}
            className="max-w-180 flex-1"
          />
          <ActionMenu
            actions={[
              { id: "admin", icon: "admin_panel_settings", label: t("account.admin") },
              { id: "signout", icon: "logout", label: t("auth.signOut"), separatorBefore: true },
            ]}
            onAction={(id) => (id === "admin" ? app.navigate({ screen: "admin" }) : toast(t("toasts.notInMockup")))}
            trigger={
              <button
                type="button"
                aria-label={t("account.menu")}
                className="ml-auto inline-flex size-12 shrink-0 cursor-pointer items-center justify-center rounded-full hover:bg-on-surface/8"
              >
                <Avatar name="Axel" tone={1} />
              </button>
            }
          />
        </header>
        <div className="flex min-h-0 flex-1">
          <main
            {...dragProps}
            className="relative min-w-0 flex-1 overflow-y-auto rounded-tl-lg px-4 pb-40 sm:px-6 md:pb-24"
          >
            {route.screen === "files" && <FilesScreen />}
            {route.screen === "search" && <SearchScreen key={route.query} />}
            {route.screen === "admin" && <AdminScreen />}
            {dropping && (
              <div className="pointer-events-none absolute inset-2 z-10 flex flex-col items-center justify-center gap-3 rounded-lg border-2 border-dashed border-primary bg-primary-container/90 text-center text-on-primary-container">
                <Icon name="upload" filled size={48} />
                <p className="m-0 text-headline-sm">{t("files.empty.dropTitle", { folder: folderName })}</p>
                <p className="m-0">{t("files.empty.dropText")}</p>
              </div>
            )}
          </main>
          {showDetails && (
            <DetailsPanel
              item={single}
              details={detailsFor(t, single, [t("nav.myFiles"), ...folderPath(app.nodes, single.parentId).map((n) => n.name)].join(" / "))}
              tab={app.details.tab}
              onTabChange={(tab) => app.setDetails({ open: true, tab })}
              onClose={() => app.setDetails({ ...app.details, open: false })}
              onManageAccess={() => app.act("share", [single.id])}
              versions={{
                versions: app.versions,
                onUploadVersion: () => fileInput.current?.click(),
                onAction: (action, version) => {
                  if (action === "keep" || action === "unkeep") {
                    app.setVersions(app.versions.map((v) => (v.number === version.number ? { ...v, keep: action === "keep" } : v)));
                    toast(t(action === "keep" ? "toasts.versionKept" : "toasts.versionUnkept", { number: version.number }));
                  } else if (action === "restore") {
                    toast(t("toasts.versionRestored", { number: version.number }));
                  } else if (action === "download") {
                    toast(t("toasts.downloading", { count: 1 }));
                  } else {
                    app.setVersions(app.versions.filter((v) => v.number !== version.number));
                  }
                },
              }}
              className={cn(
                "lg:mr-4 lg:mb-4 lg:rounded-lg",
                "max-lg:fixed max-lg:inset-y-0 max-lg:right-0 max-lg:z-30 max-lg:shadow-elevation-3 max-sm:w-full",
              )}
            />
          )}
        </div>
        <BottomNav
          active={bottom}
          onNavigate={(id) =>
            app.navigate(id === "search" ? { screen: "search", query: "" } : { screen: "files", place: id, folderId: null })
          }
          className="shrink-0 md:hidden"
        />
      </div>

      {route.screen === "files" && route.place === "files" && app.uploads.length === 0 && (
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

      {app.uploads.length > 0 && (
        <UploadTray
          items={app.uploads}
          collapsed={trayCollapsed}
          onCollapsedChange={setTrayCollapsed}
          onClose={() => app.setUploads(() => [])}
          onPause={(id) => app.setUploads((list) => list.map((u) => (u.id === id ? { ...u, state: "paused" } : u)))}
          onResume={(id) => app.setUploads((list) => list.map((u) => (u.id === id ? { ...u, state: "uploading" } : u)))}
          onRetry={(id) => app.setUploads((list) => list.map((u) => (u.id === id ? { ...u, state: "uploading", error: undefined } : u)))}
          onCancel={(id) => app.setUploads((list) => list.filter((u) => u.id !== id))}
          onRemoveCopy={(id) => {
            app.setNodes((nodes) => nodes.filter((n) => n.id !== id));
            app.setUploads((list) => list.filter((u) => u.id !== id));
            toast(t("toasts.copyRemoved"));
          }}
          onShowInFolder={() => {
            setTrayCollapsed(true);
            app.navigate({ screen: "files", place: "files", folderId: route.screen === "files" ? route.folderId : null });
          }}
          className="fixed inset-x-2 bottom-22 z-20 w-auto md:inset-x-auto md:right-6 md:bottom-6 md:w-95"
        />
      )}

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
  );
}
