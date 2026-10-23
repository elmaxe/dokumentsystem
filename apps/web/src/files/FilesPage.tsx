import { contentUrl } from "@dokument/api";
import { formatFileDate } from "@dokument/core";
import {
  ActionBar,
  Breadcrumbs,
  EmptyState,
  FileGrid,
  FileList,
  IconButton,
  ActionMenu,
  isFolder,
  sortItems,
  useSnackbar,
  type DetailsTab,
  type MenuAction,
  type Sort,
  type SortKey,
} from "@dokument/ui";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { MoveDialog, NameDialog } from "./dialogs";
import { FileDetailsPanel } from "./FileDetailsPanel";
import {
  download,
  drive,
  errorMessage,
  folderQuery,
  invalidateFolders,
  toItem,
  type Item,
} from "./drive";
import { useUploads } from "./uploadsContext";

type View = "grid" | "list";

function stored<T extends string>(key: string, fallback: T, allowed: readonly T[]): T {
  try {
    const value = localStorage.getItem(key);
    return allowed.includes(value as T) ? (value as T) : fallback;
  } catch {
    return fallback;
  }
}

function store(key: string, value: string) {
  try {
    localStorage.setItem(key, value);
  } catch {
    // Private mode: the choice just isn't remembered.
  }
}

/** My files: one folder's contents, with selection, the action bar, menus and the details panel (§15.2). */
export function FilesPage({ folderId }: { folderId: string | null }) {
  const { t, i18n } = useTranslation();
  const toast = useSnackbar();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const uploads = useUploads();
  const listing = useQuery(folderQuery(folderId));
  const [selected, setSelected] = useState<string[]>([]);
  const [view, setView] = useState<View>(() => stored("dokument.view", "grid", ["grid", "list"] as const));
  const [sort, setSort] = useState<Sort>({ key: "name", dir: "asc" });
  const [details, setDetails] = useState<{ open: boolean; tab: DetailsTab }>({ open: false, tab: "info" });
  const [renaming, setRenaming] = useState<Item | null>(null);
  const [moving, setMoving] = useState<Item[]>([]);
  const versionInput = useRef<HTMLInputElement>(null);

  const items = sortItems((listing.data?.items ?? []).map(toItem), sort, i18n.language);
  const byId = new Map(items.map((item) => [item.id, item]));
  const chosen = selected.map((id) => byId.get(id)).filter((item): item is Item => !!item);
  const single = chosen.length === 1 ? chosen[0] : undefined;

  const folderPath = listing.data?.path ?? [];
  const path = [{ id: "root", name: t("nav.myFiles") }, ...folderPath];
  const open = (id: string | null) =>
    void (id ? navigate({ to: "/folders/$folderId", params: { folderId: id } }) : navigate({ to: "/" }));

  const downloadFiles = (targets: Item[]) => {
    const files = targets.filter((item) => !isFolder(item));
    files.forEach((item) => download(contentUrl(item.id)));
    if (files.length > 0) toast(t("toasts.downloading", { count: files.length }));
  };

  const act = (action: string, targets: Item[]) => {
    const first = targets[0];
    if (!first) return;
    switch (action) {
      case "open":
        if (isFolder(first)) open(first.id);
        else void navigate({ to: "/files/$fileId", params: { fileId: first.id } });
        break;
      case "download":
        downloadFiles(targets);
        break;
      case "rename":
        setRenaming(first);
        break;
      case "move":
        setMoving(targets);
        break;
      case "details":
        setSelected([first.id]);
        setDetails({ open: true, tab: "info" });
        break;
      default:
        toast(t("common.notYet"));
    }
  };

  const menu = (item: Item): MenuAction[] => [
    { id: "open", icon: isFolder(item) ? "folder_open" : "open_in_new", label: t("actions.open") },
    ...(isFolder(item) ? [] : [{ id: "download", icon: "download", label: t("actions.download"), separatorBefore: true }]),
    { id: "rename", icon: "edit", label: t("actions.rename"), separatorBefore: isFolder(item) },
    { id: "move", icon: "drive_file_move", label: t("actions.move") },
    { id: "details", icon: "info", label: t("actions.details") },
  ];

  const meta = (item: Item) =>
    t("files.modifiedOn", { date: formatFileDate(item.modified, i18n.language, { inSentence: true }) });
  const sortKeys: SortKey[] = ["name", "modified", "size", "owner"];
  const viewProps = {
    items,
    selected,
    onSelectedChange: setSelected,
    onOpen: (item: Item) => act("open", [item]),
    menuActions: menu,
    onAction: (action: string, item: Item) =>
      act(action, selected.includes(item.id) ? chosen : [item]),
    meta,
  };

  const showDetails = details.open && single;

  return (
    <div className="flex min-h-0 min-w-0 flex-1">
      <main className="relative min-w-0 flex-1 overflow-y-auto rounded-tl-lg px-4 pb-40 sm:px-6 md:pb-24">
        <div className="flex flex-col gap-4">
          <div className="flex min-h-14 items-center gap-2">
            {chosen.length > 0 && (
              <ActionBar
                count={chosen.length}
                actions={[
                  { id: "download", icon: "download", label: t("actions.download") },
                  { id: "move", icon: "drive_file_move", label: t("actions.move") },
                ]}
                moreActions={[
                  ...(single ? [{ id: "rename", icon: "edit", label: t("actions.rename") }] : []),
                  { id: "details", icon: "info", label: t("actions.details") },
                ]}
                onAction={(id) => act(id, chosen)}
                onClear={() => setSelected([])}
                className="flex-1 max-sm:fixed max-sm:inset-x-3 max-sm:bottom-23 max-sm:z-20 max-sm:h-14 max-sm:shadow-elevation-1"
              />
            )}
            <Breadcrumbs
              path={path}
              onNavigate={(id) => open(id === "root" ? null : id)}
              className={chosen.length > 0 ? "flex-1 max-sm:flex-1 sm:hidden" : "flex-1"}
            />
            <div className={chosen.length > 0 ? "flex items-center gap-1 max-sm:flex sm:hidden" : "flex items-center gap-1"}>
              {view === "grid" && (
                <ActionMenu
                  actions={sortKeys.map((key) => ({
                    id: key,
                    icon: sort.key === key ? (sort.dir === "asc" ? "arrow_upward" : "arrow_downward") : "sort",
                    label: t(`files.columns.${key}`),
                  }))}
                  onAction={(key) => setSort({ key, dir: sort.key === key && sort.dir === "asc" ? "desc" : "asc" })}
                  trigger={<IconButton icon="sort" label={t("files.sortBy")} />}
                />
              )}
              <IconButton
                icon={view === "grid" ? "view_list" : "grid_view"}
                label={view === "grid" ? t("files.listView") : t("files.gridView")}
                onClick={() => {
                  const next = view === "grid" ? "list" : "grid";
                  setView(next);
                  store("dokument.view", next);
                }}
              />
              <IconButton
                icon="info"
                label={t("files.showDetails")}
                selected={details.open}
                onClick={() => setDetails({ ...details, open: !details.open })}
                className="max-lg:hidden"
              />
            </div>
          </div>

          {listing.isError ? (
            <EmptyState icon="error" title={errorMessage(t, listing.error)} className="mt-4" />
          ) : listing.isPending ? null : items.length === 0 ? (
            <EmptyState
              icon="folder_open"
              title={t("files.empty.folderTitle")}
              text={t("files.empty.folderText")}
              className="mt-4"
            />
          ) : view === "grid" ? (
            <FileGrid {...viewProps} />
          ) : (
            <FileList {...viewProps} sort={sort} onSortChange={setSort} />
          )}
        </div>
      </main>

      {showDetails && (
        <FileDetailsPanel
          item={single}
          folderPath={folderPath}
          tab={details.tab}
          onTabChange={(tab) => setDetails({ open: true, tab })}
          onClose={() => setDetails({ ...details, open: false })}
          onUploadVersion={() => versionInput.current?.click()}
          className="lg:mr-4 lg:mb-4 lg:rounded-lg max-lg:fixed max-lg:inset-y-0 max-lg:right-0 max-lg:z-30 max-lg:shadow-elevation-3 max-sm:w-full"
        />
      )}

      <NameDialog
        key={renaming?.id}
        open={!!renaming}
        onOpenChange={(isOpen) => !isOpen && setRenaming(null)}
        title={t("dialogs.rename.title")}
        label={t("dialogs.rename.label")}
        submitLabel={t("dialogs.rename.save")}
        initial={renaming?.name ?? ""}
        onSubmit={async (name) => {
          if (!renaming) return null;
          try {
            await drive.rename(renaming.id, name);
            await invalidateFolders(queryClient, [folderId]);
            toast(t("toasts.renamed"));
            return null;
          } catch (error) {
            return errorMessage(t, error);
          }
        }}
      />

      <MoveDialog
        key={moving.map((item) => item.id).join()}
        open={moving.length > 0}
        onOpenChange={(isOpen) => !isOpen && setMoving([])}
        items={moving}
        startIn={folderId}
        onMove={async (target) => {
          try {
            for (const item of moving) await drive.move(item.id, target);
            toast(t("toasts.moved", { count: moving.length }));
            setSelected([]);
          } catch (error) {
            toast(errorMessage(t, error));
          }
          setMoving([]);
          await invalidateFolders(queryClient, [folderId, target]);
        }}
      />

      <input
        ref={versionInput}
        type="file"
        hidden
        onChange={(event) => {
          const file = event.target.files?.[0];
          if (file && single) {
            uploads.start([file], {
              nodeId: single.id,
              folderId,
              folderName: listing.data?.folder?.name ?? t("nav.myFiles"),
            });
          }
          event.target.value = "";
        }}
      />
    </div>
  );
}
