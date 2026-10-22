import { formatFileDate } from "@dokument/core";
import { useTranslation } from "react-i18next";
import { ActionBar } from "../components/ActionBar";
import { Banner } from "../components/Banner";
import { Breadcrumbs } from "../components/Breadcrumbs";
import { Button, IconButton } from "../components/Button";
import { EmptyState } from "../components/EmptyState";
import { FileGrid } from "../components/FileGrid";
import { FileList } from "../components/FileList";
import { ActionMenu } from "../components/Menu";
import { places } from "../lib/places";
import { sortItems, type SortKey } from "../lib/files";
import { fileMenu, selectionActions, selectionMore } from "./actions";
import type { Node } from "./data";
import { folderPath, useMockup } from "./store";

/** My files, Shared with me, Recent, Starred and Trash: one file area with different contents. */
export function FilesScreen() {
  const { t, i18n } = useTranslation();
  const app = useMockup();
  if (app.route.screen !== "files") return null;
  const { place, folderId } = app.route;
  const trash = place === "trash";

  const live = app.nodes.filter((n) => n.trashedDaysLeft === undefined);
  let items: Node[];
  switch (place) {
    case "files":
      items = live.filter((n) => n.parentId === folderId && (folderId !== null || !n.sharedWithMe));
      break;
    case "shared":
      items = folderId ? live.filter((n) => n.parentId === folderId) : live.filter((n) => n.sharedWithMe);
      break;
    case "recent":
      items = live.filter((n) => n.openedAt).sort((a, b) => (b.openedAt ?? "").localeCompare(a.openedAt ?? ""));
      break;
    case "starred":
      items = live.filter((n) => n.starred);
      break;
    case "trash":
      items = app.nodes.filter((n) => n.trashedDaysLeft !== undefined);
      break;
  }
  if (place !== "recent") items = sortItems(items, app.sort, i18n.language);

  const meta = (item: Node) =>
    item.trashedDaysLeft !== undefined
      ? t("files.removedIn", { count: item.trashedDaysLeft })
      : place === "recent" && item.openedAt
        ? t("files.openedOn", { date: formatFileDate(item.openedAt, i18n.language, { inSentence: true }) })
        : t("files.modifiedOn", { date: formatFileDate(item.modified, i18n.language, { inSentence: true }) });

  const placeTitle = t(places.find((p) => p.id === place)?.key ?? "nav.myFiles");
  const path = [
    { id: "root", name: placeTitle },
    ...folderPath(app.nodes, folderId).map((n) => ({ id: n.id, name: n.name })),
  ];

  const empty = {
    files: ["folderTitle", "folderText", "folder_open"],
    shared: ["sharedTitle", "sharedText", "folder_shared"],
    recent: ["folderTitle", "folderText", "schedule"],
    starred: ["starredTitle", "starredText", "star"],
    trash: ["trashTitle", "trashText", "delete"],
  }[place];

  const sortKeys: SortKey[] = ["name", "modified", "size", "owner"];
  const view = {
    items,
    selected: app.selected,
    onSelectedChange: app.setSelected,
    onOpen: (item: Node) => app.act(trash ? "restore" : "open", [item.id]),
    onTrash: (ids: string[]) => !trash && app.act("trash", ids),
    menuActions: (item: Node) => fileMenu(t, item),
    onAction: (action: string, item: Node) =>
      app.act(action, app.selected.includes(item.id) ? app.selected : [item.id]),
    meta,
  };

  return (
    <div className="flex flex-col gap-4">
      <div className="flex min-h-14 items-center gap-2">
        {app.selected.length > 0 ? (
          <ActionBar
            count={app.selected.length}
            actions={selectionActions(t, trash)}
            moreActions={trash ? undefined : selectionMore(t)}
            onAction={(id) => app.act(id, app.selected)}
            onClear={() => app.setSelected([])}
            className="flex-1 max-sm:fixed max-sm:inset-x-3 max-sm:bottom-23 max-sm:z-20 max-sm:h-14 max-sm:shadow-elevation-1"
          />
        ) : null}
        <Breadcrumbs
          path={path}
          onNavigate={(id) => app.navigate({ screen: "files", place, folderId: id === "root" ? null : id })}
          className={app.selected.length > 0 ? "flex-1 max-sm:flex-1 sm:hidden" : "flex-1"}
        />
        <div className={app.selected.length > 0 ? "flex items-center gap-1 max-sm:flex sm:hidden" : "flex items-center gap-1"}>
          {app.view === "grid" && (
            <ActionMenu
              actions={sortKeys.map((key) => ({
                id: key,
                icon: app.sort.key === key ? (app.sort.dir === "asc" ? "arrow_upward" : "arrow_downward") : "sort",
                label: t(`files.columns.${key}`),
              }))}
              onAction={(key) =>
                app.setSort({ key, dir: app.sort.key === key && app.sort.dir === "asc" ? "desc" : "asc" })
              }
              trigger={<IconButton icon="sort" label={t("files.sortBy")} />}
            />
          )}
          <IconButton
            icon={app.view === "grid" ? "view_list" : "grid_view"}
            label={app.view === "grid" ? t("files.listView") : t("files.gridView")}
            onClick={() => app.setView(app.view === "grid" ? "list" : "grid")}
          />
          <IconButton
            icon="info"
            label={t("files.showDetails")}
            selected={app.details.open}
            onClick={() => app.setDetails({ ...app.details, open: !app.details.open })}
            className="max-lg:hidden"
          />
        </div>
      </div>

      {trash && items.length > 0 && (
        <Banner tone="info" action={<Button variant="text" onClick={() => app.act("deleteForever", items.map((i) => i.id))}>{t("files.emptyTrash")}</Button>}>
          {t("files.trashNote")}
        </Banner>
      )}

      {items.length === 0 ? (
        <EmptyState
          icon={empty[2]}
          title={t(`files.empty.${empty[0]}`)}
          text={t(`files.empty.${empty[1]}`)}
          className="mt-4"
        />
      ) : app.view === "grid" ? (
        <FileGrid {...view} />
      ) : (
        <FileList {...view} sort={app.sort} onSortChange={app.setSort} />
      )}
    </div>
  );
}
