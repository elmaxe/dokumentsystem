import { contentUrl, type PathEntry, type RecentAction } from "@dokument/api";
import {
  ActionBar,
  Breadcrumbs,
  Button,
  EmptyState,
  FileGrid,
  FileList,
  IconButton,
  Icon,
  ActionMenu,
  isFolder,
  sortItems,
  useFormat,
  useSnackbar,
  type DetailsTab,
  type FileColumn,
  type MenuAction,
  type Sort,
  type SortKey,
} from "@dokument/ui";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { useRemoteFileChanges } from "../live";
import { MoveDialog, NameDialog } from "./dialogs";
import { FileDetailsPanel } from "./FileDetailsPanel";
import {
  ApiError,
  download,
  drive,
  errorMessage,
  folderQuery,
  invalidateFolders,
  invalidateStars,
  invalidateTrash,
  recentQuery,
  starredQuery,
  toItem,
  type Item,
} from "./drive";
import { BulkTagsDialog } from "./tags";
import { useUploads } from "./uploadsContext";

type View = "grid" | "list";

/** What the page lists: one folder of My files, or everything starred or recent wherever it is (§15.2). */
export type FilesSource = { place: "folder"; folderId: string | null } | { place: "starred" } | { place: "recent" };

/** An item with where it lives, which Starred and Recent show because their items come from many folders. */
type Row = Item & { path?: PathEntry[]; recent?: { at: string; action: RecentAction } };

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

/** How long an item that arrived from elsewhere stays outlined. */
const arrivalHighlight = 2_000;

/**
 * The items that just appeared because of a change made somewhere else (§15.4), so they can be outlined for a moment.
 * The person's own uploads and new folders aren't counted: no live notice comes for them.
 */
function useArrivals(ids: string[], scope: string, ready: boolean): Set<string> {
  const remoteChanges = useRemoteFileChanges();
  const seen = useRef<{ scope: string; key: string; remoteChanges: number } | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined);
  const [arrived, setArrived] = useState<Set<string>>(() => new Set());
  const key = ids.join();

  useEffect(() => {
    if (!ready) return;
    const before = seen.current;
    // A notice comes first and the refetch it starts later, so only a new set of items is compared.
    if (before?.scope === scope && before.key === key) return;
    seen.current = { scope, key, remoteChanges };
    if (!before || before.scope !== scope || before.remoteChanges === remoteChanges) return;
    const was = new Set(before.key.split(","));
    const added = ids.filter((id) => !was.has(id));
    if (added.length === 0) return;
    setArrived(new Set(added));
    clearTimeout(timer.current);
    timer.current = setTimeout(() => setArrived(new Set()), arrivalHighlight);
  }, [key, scope, ready, remoteChanges, ids]);

  useEffect(() => () => clearTimeout(timer.current), []);
  return arrived;
}

const activityIcons: Record<RecentAction, string> = {
  opened: "visibility",
  downloaded: "download",
  uploaded: "upload",
  restored: "history",
};

/**
 * My files, Starred and Recent: a folder's contents, or the person's starred or recent items, with selection, the
 * action bar, menus and the details panel (§15.2).
 */
export function FilesPage({ source }: { source: FilesSource }) {
  const { t, i18n } = useTranslation();
  const format = useFormat();
  const toast = useSnackbar();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const uploads = useUploads();
  const folderId = source.place === "folder" ? source.folderId : null;
  const listing = useQuery({ ...folderQuery(folderId), enabled: source.place === "folder" });
  const starred = useQuery({ ...starredQuery, enabled: source.place === "starred" });
  const recent = useQuery({ ...recentQuery, enabled: source.place === "recent" });
  const query = source.place === "folder" ? listing : source.place === "starred" ? starred : recent;
  const [selected, setSelected] = useState<string[]>([]);
  const [view, setView] = useState<View>(() => stored("dokument.view", "grid", ["grid", "list"] as const));
  const [sort, setSort] = useState<Sort>({ key: "name", dir: "asc" });
  const [details, setDetails] = useState<{ open: boolean; tab: DetailsTab }>({ open: false, tab: "info" });
  const [renaming, setRenaming] = useState<Item | null>(null);
  const [moving, setMoving] = useState<Item[]>([]);
  const [tagging, setTagging] = useState<Item[]>([]);
  const versionInput = useRef<HTMLInputElement>(null);

  const rows: Row[] =
    source.place === "folder"
      ? (listing.data?.items ?? []).map(toItem)
      : source.place === "starred"
        ? (starred.data?.items ?? []).map((s) => ({ ...toItem(s.node), path: s.path }))
        : (recent.data?.items ?? []).map((r) => ({
            ...toItem(r.node),
            path: r.path,
            recent: { at: r.at, action: r.action },
          }));
  const arrived = useArrivals(
    rows.map((row) => row.id),
    source.place === "folder" ? `folder:${folderId ?? "root"}` : source.place,
    query.isSuccess,
  );
  const marked = arrived.size > 0 ? rows.map((row) => (arrived.has(row.id) ? { ...row, arrived: true } : row)) : rows;
  // Recent keeps the server's newest-first order; the others sort like a folder.
  const items = source.place === "recent" ? marked : sortItems(marked, sort, i18n.language);
  const byId = new Map(items.map((item) => [item.id, item]));
  const chosen = selected.map((id) => byId.get(id)).filter((item): item is Row => !!item);
  const single = chosen.length === 1 ? chosen[0] : undefined;

  const folderPath = listing.data?.path ?? [];
  const path = [{ id: "root", name: t("nav.myFiles") }, ...folderPath];
  const open = (id: string | null) =>
    void (id ? navigate({ to: "/folders/$folderId", params: { folderId: id } }) : navigate({ to: "/" }));
  /** The folders above an item: the folder's path in My files, or the item's own in Starred and Recent. */
  const pathOf = (item: Row) => item.path ?? folderPath;
  const where = (item: Row) => pathOf(item).at(-1)?.name ?? t("nav.myFiles");

  const downloadFiles = (targets: Item[]) => {
    const files = targets.filter((item) => !isFolder(item));
    files.forEach((item) => download(contentUrl(item.id)));
    if (files.length > 0) toast(t("toasts.downloading", { count: files.length }));
  };

  const trash = async (targets: Item[]) => {
    const ids = targets.map((item) => item.id);
    try {
      await drive.trash(ids);
      setSelected([]);
      toast(t("toasts.trashed", { count: ids.length }), {
        label: t("common.undo"),
        onClick: () =>
          void drive
            .restore(ids)
            .catch((error: unknown) => toast(errorMessage(t, error)))
            .finally(() => void invalidateTrash(queryClient)),
      });
    } catch (error) {
      toast(errorMessage(t, error));
    }
    await invalidateTrash(queryClient);
  };

  const star = async (targets: Item[], on: boolean) => {
    const ids = targets.map((item) => item.id);
    try {
      await drive.star(ids, on);
      // Unstarring on the Starred page takes the items off it.
      if (!on && source.place === "starred") setSelected([]);
      toast(t(on ? "toasts.starred" : "toasts.unstarred", { count: ids.length }), {
        label: t("common.undo"),
        onClick: () =>
          void drive
            .star(ids, !on)
            .catch((error: unknown) => toast(errorMessage(t, error)))
            .finally(() => void invalidateStars(queryClient)),
      });
    } catch (error) {
      toast(errorMessage(t, error));
    }
    await invalidateStars(queryClient);
  };

  const act = (action: string, targets: Row[]) => {
    const first = targets[0];
    if (!first) return;
    switch (action) {
      case "open":
        if (isFolder(first)) open(first.id);
        else
          void navigate({
            to: "/files/$fileId",
            params: { fileId: first.id },
            search: source.place === "folder" ? {} : { from: source.place },
          });
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
      case "tags":
        setTagging(targets.filter((item) => !isFolder(item)));
        break;
      case "star":
      case "unstar":
        void star(targets, action === "star");
        break;
      case "showInFolder":
        open(first.node.parentId ?? null);
        break;
      case "details":
        setSelected([first.id]);
        setDetails({ open: true, tab: "info" });
        break;
      case "trash":
        void trash(targets);
        break;
      default:
        toast(t("common.notYet"));
    }
  };

  const starAction = (targets: Item[]): MenuAction =>
    targets.every((item) => item.starred)
      ? { id: "unstar", icon: "star", label: t("actions.unstar") }
      : { id: "star", icon: "star", label: t("actions.star") };

  const menu = (item: Row): MenuAction[] => [
    { id: "open", icon: isFolder(item) ? "folder_open" : "open_in_new", label: t("actions.open") },
    ...(isFolder(item) ? [] : [{ id: "download", icon: "download", label: t("actions.download"), separatorBefore: true }]),
    { id: "rename", icon: "edit", label: t("actions.rename"), separatorBefore: isFolder(item) },
    { id: "move", icon: "drive_file_move", label: t("actions.move") },
    ...(isFolder(item) ? [] : [{ id: "tags", icon: "sell", label: t("tags.action") }]),
    starAction([item]),
    ...(source.place === "folder" ? [] : [{ id: "showInFolder", icon: "folder_open", label: t("actions.showInFolder") }]),
    { id: "details", icon: "info", label: t("actions.details") },
    { id: "trash", icon: "delete", label: t("actions.trash"), separatorBefore: true },
  ];

  const activity = (item: Row) =>
    item.recent &&
    t(`files.activity.${item.recent.action}`, { date: format.date(item.recent.at, { inSentence: true }) });
  const meta = (item: Row) =>
    source.place === "recent"
      ? activity(item)
      : t("files.modifiedOn", { date: format.date(item.modified, { inSentence: true }) });

  const locationColumn: FileColumn<Row> = {
    id: "location",
    label: t("files.columns.location"),
    className: "w-[20%] truncate @max-[600px]:hidden",
    cell: (item) => {
      const crumbs = [t("nav.myFiles"), ...pathOf(item).map((p) => p.name)];
      return (
        <button
          type="button"
          title={crumbs.join(" › ")}
          onClick={(event) => {
            event.stopPropagation();
            open(item.node.parentId ?? null);
          }}
          onDoubleClick={(event) => event.stopPropagation()}
          className="inline-flex max-w-full cursor-pointer items-center gap-2 rounded-xs text-inherit hover:underline"
        >
          <Icon name="folder" size={18} />
          <span className="truncate">{crumbs.length > 1 ? crumbs.slice(1).join(" › ") : crumbs[0]}</span>
        </button>
      );
    },
  };
  const sizeColumn: FileColumn<Row> = {
    id: "size",
    label: t("files.columns.size"),
    className: "w-[11%] tabular truncate text-right @max-[600px]:hidden",
    // An empty folder has nothing to measure, as in My files.
    cell: (item) =>
      item.size !== undefined && !(item.contents?.files === 0 && item.contents.folders === 0)
        ? format.bytes(item.size)
        : "—",
    sortKey: "size",
  };
  const columns: FileColumn<Row>[] | undefined =
    source.place === "starred"
      ? [
          locationColumn,
          {
            id: "modified",
            label: t("files.columns.modified"),
            className: "w-[16%] tabular truncate @max-[600px]:hidden",
            cell: (item) => <span title={format.fullDate(item.modified)}>{format.date(item.modified)}</span>,
            sortKey: "modified",
          },
          sizeColumn,
        ]
      : source.place === "recent"
        ? [
            locationColumn,
            {
              id: "activity",
              label: t("files.columns.activity"),
              className: "w-[28%] tabular truncate @max-[600px]:hidden",
              cell: (item) =>
                item.recent && (
                  <span title={format.fullDate(item.recent.at)} className="inline-flex max-w-full items-center gap-2">
                    <Icon name={activityIcons[item.recent.action]} size={18} />
                    <span className="truncate">{activity(item)}</span>
                  </span>
                ),
            },
            { ...sizeColumn, sortKey: undefined },
          ]
        : undefined;

  const sortKeys: SortKey[] = source.place === "starred" ? ["name", "modified", "size"] : ["name", "modified", "size", "owner"];
  const viewProps = {
    items,
    selected,
    onSelectedChange: setSelected,
    onOpen: (item: Row) => act("open", [item]),
    onTrash: (ids: string[]) => act("trash", ids.map((id) => byId.get(id)).filter((item): item is Row => !!item)),
    menuActions: menu,
    onAction: (action: string, item: Row) => act(action, selected.includes(item.id) ? chosen : [item]),
    meta,
  };

  const showDetails = details.open && single;
  // Gone while open, perhaps trashed on another device, or a link to a folder that no longer exists.
  const folderGone =
    source.place === "folder" && listing.isError && listing.error instanceof ApiError && listing.error.status === 404;
  const title = source.place === "starred" ? t("nav.starred") : t("nav.recent");
  const empty =
    source.place === "folder"
      ? { icon: "folder_open", title: t("files.empty.folderTitle"), text: t("files.empty.folderText") }
      : source.place === "starred"
        ? { icon: "star", title: t("files.empty.starredTitle"), text: t("files.empty.starredText") }
        : { icon: "schedule", title: t("files.empty.recentTitle"), text: t("files.empty.recentText") };

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
                  ...(chosen.some((item) => !isFolder(item)) ? [{ id: "tags", icon: "sell", label: t("tags.action") }] : []),
                  { id: "trash", icon: "delete", label: t("actions.trash") },
                ]}
                moreActions={[
                  starAction(chosen),
                  ...(single ? [{ id: "rename", icon: "edit", label: t("actions.rename") }] : []),
                  { id: "details", icon: "info", label: t("actions.details") },
                ]}
                onAction={(id) => act(id, chosen)}
                onClear={() => setSelected([])}
                className="flex-1 max-sm:fixed max-sm:inset-x-3 max-sm:bottom-23 max-sm:z-20 max-sm:h-14 max-sm:shadow-elevation-1"
              />
            )}
            {source.place === "folder" ? (
              <Breadcrumbs
                path={path}
                onNavigate={(id) => open(id === "root" ? null : id)}
                className={chosen.length > 0 ? "flex-1 max-sm:flex-1 sm:hidden" : "flex-1"}
              />
            ) : (
              <h1 className={chosen.length > 0 ? "m-0 flex-1 text-headline-sm max-sm:block sm:hidden" : "m-0 flex-1 text-headline-sm"}>
                {title}
              </h1>
            )}
            <div className={chosen.length > 0 ? "flex items-center gap-1 max-sm:flex sm:hidden" : "flex items-center gap-1"}>
              {view === "grid" && source.place !== "recent" && (
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

          {folderGone ? (
            <EmptyState
              icon="folder_delete"
              title={t("live.folderGoneTitle")}
              text={t("live.folderGoneText")}
              action={
                <div className="flex flex-wrap justify-center gap-2">
                  <Button variant="outlined" onClick={() => void navigate({ to: "/trash" })}>
                    {t("live.openTrash")}
                  </Button>
                  <Button onClick={() => open(null)}>{t("live.goToMyFiles")}</Button>
                </div>
              }
              className="mt-4"
            />
          ) : query.isError ? (
            <EmptyState icon="error" title={errorMessage(t, query.error)} className="mt-4" />
          ) : query.isPending ? null : items.length === 0 ? (
            <EmptyState icon={empty.icon} title={empty.title} text={empty.text} className="mt-4" />
          ) : view === "grid" ? (
            <FileGrid {...viewProps} />
          ) : (
            <FileList
              {...viewProps}
              sort={source.place === "recent" ? undefined : sort}
              onSortChange={source.place === "recent" ? undefined : setSort}
              columns={columns}
            />
          )}
        </div>
      </main>

      {showDetails && (
        <FileDetailsPanel
          item={single}
          folderPath={pathOf(single)}
          tab={details.tab}
          onTabChange={(tab) => setDetails({ open: true, tab })}
          onClose={() => setDetails({ ...details, open: false })}
          onUploadVersion={() => versionInput.current?.click()}
          onStarChange={(on) => void star([single], on)}
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
            await invalidateFolders(queryClient, [renaming.node.parentId ?? null]);
            toast(t("toasts.renamed"));
            return null;
          } catch (error) {
            return errorMessage(t, error);
          }
        }}
      />

      <BulkTagsDialog items={tagging} onClose={() => setTagging([])} />

      <MoveDialog
        key={moving.map((item) => item.id).join()}
        open={moving.length > 0}
        onOpenChange={(isOpen) => !isOpen && setMoving([])}
        items={moving}
        startIn={moving[0]?.node.parentId ?? folderId}
        onMove={async (target) => {
          try {
            for (const item of moving) await drive.move(item.id, target);
            toast(t("toasts.moved", { count: moving.length }));
            setSelected([]);
          } catch (error) {
            toast(errorMessage(t, error));
          }
          setMoving([]);
          await invalidateFolders(queryClient, [...moving.map((item) => item.node.parentId ?? null), target]);
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
              folderId: single.node.parentId ?? null,
              folderName: where(single),
            });
          }
          event.target.value = "";
        }}
      />
    </div>
  );
}
