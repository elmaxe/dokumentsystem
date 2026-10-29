import { contentUrl, type PathEntry, type Person, type RecentAction, type ShareRole } from "@dokument/api";
import {
  ActionBar,
  Breadcrumbs,
  Button,
  Dialog,
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
import { sessionQuery } from "../api";
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
  sharedQuery,
  starredQuery,
  toItem,
  type Item,
} from "./drive";
import { ShareNodeDialog } from "./ShareNodeDialog";
import { canEdit, invalidateSharing } from "./sharing";
import { BulkTagsDialog } from "./tags";
import { useUploads } from "./uploadsContext";

type View = "grid" | "list";

/**
 * What the page lists: one folder, yours or one shared with you, or everything starred, recent or shared with you
 * wherever it is (§9, §15.2).
 */
export type FilesSource =
  | { place: "folder"; folderId: string | null }
  | { place: "starred" }
  | { place: "recent" }
  | { place: "shared" };

/** An item with where it lives, which Starred and Recent show because their items come from many folders. */
type Row = Item & {
  path?: PathEntry[];
  recent?: { at: string; action: RecentAction };
  share?: { at: string; role: ShareRole };
};

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
 * My files, Shared with me, Starred and Recent: a folder's contents, or the person's shared, starred or recent items,
 * with selection, the action bar, menus and the details panel (§15.2). What each item offers follows the person's
 * access to it (§9).
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
  const shared = useQuery({ ...sharedQuery, enabled: source.place === "shared" });
  const query =
    source.place === "folder" ? listing : source.place === "starred" ? starred : source.place === "shared" ? shared : recent;
  const me = useQuery(sessionQuery).data?.user.id;
  const [selected, setSelected] = useState<string[]>([]);
  const [view, setView] = useState<View>(() => stored("dokument.view", "grid", ["grid", "list"] as const));
  const [sort, setSort] = useState<Sort>({ key: "name", dir: "asc" });
  const [details, setDetails] = useState<{ open: boolean; tab: DetailsTab }>({ open: false, tab: "info" });
  const [renaming, setRenaming] = useState<Item | null>(null);
  const [moving, setMoving] = useState<Item[]>([]);
  const [tagging, setTagging] = useState<Item[]>([]);
  const [sharing, setSharing] = useState<Item | null>(null);
  const [leaving, setLeaving] = useState<Item | null>(null);
  const [losing, setLosing] = useState<{ items: Item[]; target: string | null; people: Person[] } | null>(null);
  const versionInput = useRef<HTMLInputElement>(null);

  const rows: Row[] =
    source.place === "folder"
      ? (listing.data?.items ?? []).map(toItem)
      : source.place === "starred"
        ? (starred.data?.items ?? []).map((s) => ({ ...toItem(s.node), path: s.path }))
        : source.place === "shared"
          ? (shared.data ?? []).map((s) => ({
              ...toItem(s.node),
              path: [],
              isNew: s.new,
              share: { at: s.sharedAt, role: s.role },
            }))
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
  const folder = listing.data?.folder ?? undefined;
  /** In a folder someone shared: its path starts at the top folder the person can see, under Shared with me. */
  const othersFolder = !!folder && !!me && folder.ownerId !== me;
  const rootName = (ownerId: string | undefined) =>
    ownerId && me && ownerId !== me ? t("nav.sharedWithMe") : t("nav.myFiles");
  const path = [{ id: "root", name: rootName(folder?.ownerId) }, ...folderPath];
  const open = (id: string | null) =>
    void (id ? navigate({ to: "/folders/$folderId", params: { folderId: id } }) : navigate({ to: "/" }));
  /** The folders above an item: the folder's path in My files, or the item's own in Starred and Recent. */
  const pathOf = (item: Row) => item.path ?? folderPath;
  const where = (item: Row) => pathOf(item).at(-1)?.name ?? rootName(item.node.ownerId);
  /** The folder above an item, or for one at the top of what the person can see, where it's listed. */
  const openParent = (item: Row) =>
    item.node.ownerId !== me && pathOf(item).length === 0
      ? void navigate({ to: "/shared" })
      : open(item.node.parentId ?? null);
  const editable = (targets: Item[]) => targets.length > 0 && targets.every((item) => canEdit(item.node.access));
  /** Within someone else's files, moves stay inside the top folder the person can see (§9). */
  const moveRoot = (item: Row) => (item.node.ownerId === me ? null : pathOf(item)[0]);
  const movable = (targets: Row[]) =>
    editable(targets) &&
    targets.every((item) => item.node.ownerId === me || !!moveRoot(item)) &&
    new Set(targets.map((item) => moveRoot(item)?.id ?? "mine")).size === 1;

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

  const move = async (targets: Item[], target: string | null) => {
    try {
      for (const item of targets) await drive.move(item.id, target);
      toast(t("toasts.moved", { count: targets.length }));
      setSelected([]);
    } catch (error) {
      toast(errorMessage(t, error));
    }
    await invalidateFolders(queryClient, [...targets.map((item) => item.node.parentId ?? null), target]);
  };

  const leave = async (item: Item) => {
    try {
      await drive.leave(item.id);
      setSelected([]);
      toast(t("toasts.left", { name: item.name }));
    } catch (error) {
      toast(errorMessage(t, error));
    }
    await invalidateSharing(queryClient);
  };

  const act = (action: string, targets: Row[]) => {
    const first = targets[0];
    if (!first) return;
    switch (action) {
      case "open":
        // Opening something shared with the person takes the New off it.
        if (first.isNew)
          void drive
            .seen(first.id)
            .then(() => queryClient.invalidateQueries({ queryKey: ["shared"] }))
            .catch(() => {});
        if (isFolder(first)) open(first.id);
        else
          void navigate({
            to: "/files/$fileId",
            params: { fileId: first.id },
            search: source.place === "folder" || source.place === "shared" ? {} : { from: source.place },
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
        openParent(first);
        break;
      case "share":
        setSharing(first);
        break;
      case "leave":
        setLeaving(first);
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

  const menu = (item: Row): MenuAction[] => {
    const edit = editable([item]);
    return [
      { id: "open", icon: isFolder(item) ? "folder_open" : "open_in_new", label: t("actions.open") },
      ...(item.node.access === "owner"
        ? [{ id: "share", icon: "person_add", label: t("actions.share"), separatorBefore: true }]
        : []),
      ...(isFolder(item)
        ? []
        : [{ id: "download", icon: "download", label: t("actions.download"), separatorBefore: item.node.access !== "owner" }]),
      ...(edit ? [{ id: "rename", icon: "edit", label: t("actions.rename"), separatorBefore: isFolder(item) }] : []),
      ...(movable([item]) ? [{ id: "move", icon: "drive_file_move", label: t("actions.move") }] : []),
      ...(edit && !isFolder(item) ? [{ id: "tags", icon: "sell", label: t("tags.action") }] : []),
      starAction([item]),
      ...(source.place === "folder" || source.place === "shared"
        ? []
        : [{ id: "showInFolder", icon: "folder_open", label: t("actions.showInFolder") }]),
      { id: "details", icon: "info", label: t("actions.details") },
      ...(source.place === "shared"
        ? [{ id: "leave", icon: "person_remove", label: t("share.leave"), separatorBefore: true }]
        : edit
          ? [{ id: "trash", icon: "delete", label: t("actions.trash"), separatorBefore: true }]
          : []),
    ];
  };

  const activity = (item: Row) =>
    item.recent &&
    t(`files.activity.${item.recent.action}`, { date: format.date(item.recent.at, { inSentence: true }) });
  const meta = (item: Row) =>
    source.place === "recent"
      ? activity(item)
      : source.place === "shared"
        ? item.owner
        : t("files.modifiedOn", { date: format.date(item.modified, { inSentence: true }) });

  const locationColumn: FileColumn<Row> = {
    id: "location",
    label: t("files.columns.location"),
    className: "w-[20%] truncate @max-[600px]:hidden",
    cell: (item) => {
      const crumbs = [rootName(item.node.ownerId), ...pathOf(item).map((p) => p.name)];
      return (
        <button
          type="button"
          title={crumbs.join(" › ")}
          onClick={(event) => {
            event.stopPropagation();
            openParent(item);
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
    source.place === "shared"
      ? [
          {
            id: "sharedBy",
            label: t("files.columns.sharedBy"),
            className: "w-[20%] truncate @max-[600px]:hidden",
            cell: (item) => item.owner,
            sortKey: "owner",
          },
          {
            id: "sharedAt",
            label: t("files.columns.sharedAt"),
            className: "w-[16%] tabular truncate @max-[600px]:hidden",
            cell: (item) => item.share && <span title={format.fullDate(item.share.at)}>{format.date(item.share.at)}</span>,
          },
          {
            id: "access",
            label: t("files.columns.access"),
            className: "w-[14%] truncate @max-[600px]:hidden",
            cell: (item) => t(`share.roles.${item.node.access}`),
          },
        ]
      : source.place === "starred"
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

  const sortKeys: SortKey[] =
    source.place === "starred" ? ["name", "modified", "size"] : ["name", "modified", "size", "owner"];
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
  const title =
    source.place === "starred" ? t("nav.starred") : source.place === "shared" ? t("nav.sharedWithMe") : t("nav.recent");
  const empty =
    source.place === "folder"
      ? {
          icon: "folder_open",
          title: t("files.empty.folderTitle"),
          text: t(folder && !canEdit(folder.access) ? "files.empty.folderViewOnlyText" : "files.empty.folderText"),
        }
      : source.place === "starred"
        ? { icon: "star", title: t("files.empty.starredTitle"), text: t("files.empty.starredText") }
        : source.place === "shared"
          ? { icon: "folder_shared", title: t("files.empty.sharedTitle"), text: t("files.empty.sharedText") }
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
                  ...(movable(chosen) ? [{ id: "move", icon: "drive_file_move", label: t("actions.move") }] : []),
                  ...(editable(chosen) && chosen.some((item) => !isFolder(item))
                    ? [{ id: "tags", icon: "sell", label: t("tags.action") }]
                    : []),
                  ...(editable(chosen) && source.place !== "shared"
                    ? [{ id: "trash", icon: "delete", label: t("actions.trash") }]
                    : []),
                ]}
                moreActions={[
                  ...(single?.node.access === "owner"
                    ? [{ id: "share", icon: "person_add", label: t("actions.share") }]
                    : []),
                  starAction(chosen),
                  ...(single && editable([single]) ? [{ id: "rename", icon: "edit", label: t("actions.rename") }] : []),
                  { id: "details", icon: "info", label: t("actions.details") },
                  ...(single && source.place === "shared"
                    ? [{ id: "leave", icon: "person_remove", label: t("share.leave") }]
                    : []),
                ]}
                onAction={(id) => act(id, chosen)}
                onClear={() => setSelected([])}
                className="flex-1 max-sm:fixed max-sm:inset-x-3 max-sm:bottom-23 max-sm:z-20 max-sm:h-14 max-sm:shadow-elevation-1"
              />
            )}
            {source.place === "folder" ? (
              <div className={chosen.length > 0 ? "flex min-w-0 flex-1 flex-col max-sm:flex sm:hidden" : "flex min-w-0 flex-1 flex-col"}>
                <Breadcrumbs
                  path={path}
                  onNavigate={(id) => (id === "root" && othersFolder ? void navigate({ to: "/shared" }) : open(id === "root" ? null : id))}
                />
                {othersFolder && folder && (
                  <span className="inline-flex items-center gap-1 text-body-sm text-on-surface-variant">
                    <Icon name={canEdit(folder.access) ? "edit" : "visibility"} size={16} />
                    {t(`share.ownerFolder.${canEdit(folder.access) ? "editor" : "viewer"}`, { name: folder.ownerName })}
                  </span>
                )}
              </div>
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
          onUploadVersion={editable([single]) ? () => versionInput.current?.click() : undefined}
          onStarChange={(on) => void star([single], on)}
          onShare={() => setSharing(single)}
          onLeave={() => setLeaving(single)}
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
        root={moving[0] && moveRoot(moving[0])}
        onMove={async (target) => {
          const targets = moving;
          // Ask first when the move would hide something from people who see it now (§9).
          const people = await drive.losingAccess(targets.map((item) => item.id), target).catch(() => []);
          setMoving([]);
          if (people.length > 0) setLosing({ items: targets, target, people });
          else await move(targets, target);
        }}
      />

      <Dialog
        open={!!losing}
        onOpenChange={(isOpen) => !isOpen && setLosing(null)}
        title={t("dialogs.accessLost.title")}
        description={
          losing &&
          t("dialogs.accessLost.text", {
            count: losing.items.length,
            names: new Intl.ListFormat(i18n.language, { type: "conjunction" }).format(losing.people.map((p) => p.name)),
          })
        }
        width="sm"
        footer={
          <>
            <Button variant="text" onClick={() => setLosing(null)}>
              {t("common.cancel")}
            </Button>
            <Button
              onClick={() => {
                if (losing) void move(losing.items, losing.target);
                setLosing(null);
              }}
            >
              {t("dialogs.accessLost.move")}
            </Button>
          </>
        }
      />

      <Dialog
        open={!!leaving}
        onOpenChange={(isOpen) => !isOpen && setLeaving(null)}
        title={leaving ? t("share.leaveTitle", { name: leaving.name }) : undefined}
        description={leaving ? t("share.leaveConfirm", { name: leaving.name, owner: leaving.owner }) : undefined}
        width="sm"
        footer={
          <>
            <Button variant="text" onClick={() => setLeaving(null)}>
              {t("common.cancel")}
            </Button>
            <Button
              variant="danger"
              onClick={() => {
                if (leaving) void leave(leaving);
                setLeaving(null);
              }}
            >
              {t("share.leave")}
            </Button>
          </>
        }
      />

      <ShareNodeDialog node={sharing} onClose={() => setSharing(null)} />

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
