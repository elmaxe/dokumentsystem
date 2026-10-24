import { useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { cn } from "../lib/cn";
import { useFormat } from "../lib/display";
import { contentsLabel, isFolder, isPaged, type FileItem, type Sort, type SortKey } from "../lib/files";
import { useFileSelection } from "../lib/useFileSelection";
import { useCoarsePointer } from "../lib/usePointer";
import { IconButton } from "./Button";
import { Checkbox } from "./Checkbox";
import { FileFlags } from "./FileFlags";
import type { FileViewProps } from "./FileGrid";
import { FileTypeIcon } from "./FileTypeIcon";
import { Icon } from "./Icon";
import { ActionContextMenu, ActionMenu } from "./Menu";

/** A column after the name. Columns with a sort key get a sortable header. */
export type FileColumn<T extends FileItem = FileItem> = {
  id: string;
  label: string;
  /** Width and alignment; add <code>@max-[600px]:hidden</code> to fold it into the second line on phones. */
  className: string;
  cell: (item: T) => ReactNode;
  sortKey?: SortKey;
};

export type FileListProps<T extends FileItem = FileItem> = FileViewProps<T> & {
  sort?: Sort;
  onSortChange?: (sort: Sort) => void;
  /** Replaces the owner / modified / size columns, as the Trash page does. */
  columns?: FileColumn<T>[];
};

/**
 * A sortable name / owner / modified / size table (§16.4). Under 600px of its own width the
 * columns fold into a second line under the name.
 */
export function FileList<T extends FileItem>({
  items,
  selected,
  onSelectedChange,
  onOpen,
  onTrash,
  menuActions,
  onAction,
  meta,
  sort,
  onSortChange,
  columns: custom,
}: FileListProps<T>) {
  const { t } = useTranslation();
  const format = useFormat();
  const coarse = useCoarsePointer();
  const { containerRef, itemProps } = useFileSelection({
    items,
    selected,
    onSelectedChange,
    onOpen,
    onTrash,
  });
  const all = items.length > 0 && selected.length === items.length;
  const some = selected.length > 0 && !all;
  // An empty folder has nothing to measure, so it shows a dash like a size the server doesn't know.
  const empty = (item: FileItem) => item.contents?.files === 0 && item.contents.folders === 0;
  const hasSize = (item: FileItem) => item.size !== undefined && !empty(item);
  const size = (item: FileItem) => (hasSize(item) ? format.bytes(item.size!) : "—");
  const date = (item: FileItem) => format.date(item.modified);
  const columns: FileColumn<T>[] = custom ?? [
    {
      id: "owner",
      label: t("files.columns.owner"),
      className: "w-[18%] truncate @max-[600px]:hidden",
      cell: (item) => item.owner,
      sortKey: "owner",
    },
    {
      id: "modified",
      label: t("files.columns.modified"),
      className: "w-[18%] tabular truncate @max-[600px]:hidden",
      cell: (item) => <span title={format.fullDate(item.modified)}>{date(item)}</span>,
      sortKey: "modified",
    },
    {
      id: "size",
      label: t("files.columns.size"),
      className: "w-[11%] tabular truncate text-right @max-[600px]:hidden",
      cell: (item) => (
        <span title={item.contents && contentsLabel(t, item.contents)}>{size(item)}</span>
      ),
      sortKey: "size",
    },
  ];
  // The <col> takes the width, and hides with the cells on phones so the name gets the room.
  const colClass = (className: string) =>
    className
      .split(" ")
      .filter((c) => c.startsWith("w-") || c === "@max-[600px]:hidden")
      .join(" ");

  return (
    <div
      ref={containerRef}
      className="@container overflow-hidden rounded-md border border-outline-variant bg-surface-container-lowest"
    >
      <table role="grid" aria-multiselectable className="w-full table-fixed border-collapse">
        {/* Column widths live here, not on the header cells, because phones hide the header row. */}
        <colgroup>
          <col className="w-11" />
          <col />
          {columns.map((column) => (
            <col key={column.id} className={colClass(column.className)} />
          ))}
          <col className="w-12" />
        </colgroup>
        <thead className="@max-[600px]:sr-only">
          <tr className="h-10 border-b border-outline-variant">
            <th className="w-11 pl-4">
              <Checkbox
                checked={all ? true : some ? "indeterminate" : false}
                onCheckedChange={(checked) => onSelectedChange(checked ? items.map((i) => i.id) : [])}
                label={t("files.selectAll")}
              />
            </th>
            <SortHeader
              label={t("files.columns.name")}
              sortKey="name"
              sort={sort}
              onSortChange={onSortChange}
            />
            {columns.map((column) => (
              <SortHeader
                key={column.id}
                label={column.label}
                sortKey={column.sortKey}
                sort={sort}
                onSortChange={onSortChange}
                className={cn(column.className.includes("text-right") && "text-right", column.className.includes("@max-[600px]:hidden") && "@max-[600px]:hidden")}
              />
            ))}
            <th className="w-12">
              <span className="sr-only">{t("files.columns.actions")}</span>
            </th>
          </tr>
        </thead>
        <tbody>
          {items.map((item) => {
            const isSelected = selected.includes(item.id);
            const actions = menuActions?.(item);
            const act = onAction && ((action: string) => onAction(action, item));
            const row = (
              <tr
                key={item.id}
                {...itemProps(item)}
                className={cn(
                  "group h-row-dense border-b border-outline-variant select-none last:border-b-0 pointer-coarse:h-touch @max-[600px]:h-auto [-webkit-touch-callout:none]",
                  isSelected
                    ? "bg-primary-container text-on-primary-container"
                    : "text-on-surface-variant hover:bg-on-surface/5",
                )}
              >
                <td className="pl-4">
                  <Checkbox
                    checked={isSelected}
                    onCheckedChange={(checked) =>
                      onSelectedChange(
                        checked ? [...selected, item.id] : selected.filter((id) => id !== item.id),
                      )
                    }
                    label={t("files.select", { name: item.name })}
                  />
                </td>
                <td className="px-3 @max-[600px]:py-2">
                  <div className="flex min-w-0 items-center gap-3">
                    <ListThumbnail item={item} />
                    <div className="flex min-w-0 flex-col">
                      <span
                        title={item.name}
                        className={cn(
                          "truncate font-medium",
                          isSelected ? "text-on-primary-container" : "text-on-surface",
                        )}
                      >
                        {item.name}
                      </span>
                      <span className="tabular hidden truncate text-body-sm @max-[600px]:block">
                        {meta?.(item) ?? [date(item), hasSize(item) && size(item)]
                          .filter(Boolean)
                          .join(" · ")}
                      </span>
                    </div>
                    <FileFlags item={item} />
                  </div>
                </td>
                {columns.map((column) => (
                  <td key={column.id} className={cn("px-3", column.className.replace(/\bw-\S+/, ""))}>
                    {column.cell(item)}
                  </td>
                ))}
                <td className="pr-2">
                  {actions && act && (
                    <ActionMenu
                      actions={actions}
                      onAction={act}
                      trigger={
                        <IconButton
                          icon="more_vert"
                          label={t("files.moreActionsFor", { name: item.name })}
                          size="sm"
                          tabIndex={-1}
                          className="text-inherit"
                          onClick={(event) => event.stopPropagation()}
                          onDoubleClick={(event) => event.stopPropagation()}
                        />
                      }
                    />
                  )}
                </td>
              </tr>
            );
            return actions && act ? (
              <ActionContextMenu key={item.id} actions={actions} onAction={act} disabled={coarse}>
                {row}
              </ActionContextMenu>
            ) : (
              row
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

/**
 * The thumbnail in place of the type icon once the worker has made one (§16.4): 32 px, 40 px on phones. Pages
 * show their top, as in the grid. Folders and files without a preview keep the icon, in a box of the same size
 * so names line up.
 */
function ListThumbnail({ item }: { item: FileItem }) {
  const [failed, setFailed] = useState(false);
  const box = "relative flex size-8 shrink-0 items-center justify-center overflow-hidden rounded-xs @max-[600px]:size-10";
  if (isFolder(item) || !item.thumbnail || failed) {
    return (
      <span className={cn(box, item.thumbnailPending && "bg-surface-container-high")}>
        <FileTypeIcon
          kind={item.kind}
          shared={item.shared}
          size={item.thumbnailPending ? "sm" : undefined}
          className="@max-[600px]:text-[28px]"
        />
        {item.thumbnailPending && (
          <span
            aria-hidden
            className="absolute inset-0 animate-shimmer bg-linear-100 from-transparent from-30% via-surface-container-lowest/60 to-transparent to-70%"
          />
        )}
      </span>
    );
  }
  return (
    <span className={cn(box, "bg-surface-container-high", isPaged(item) && "border border-outline-variant bg-surface-container-lowest")}>
      <img
        src={item.thumbnail}
        alt=""
        draggable={false}
        loading="lazy"
        onError={() => setFailed(true)}
        className={cn("size-full object-cover", isPaged(item) && "object-top")}
      />
    </span>
  );
}

function SortHeader({
  label,
  sortKey,
  sort,
  onSortChange,
  className,
}: {
  label: string;
  sortKey?: SortKey;
  sort?: Sort;
  onSortChange?: (sort: Sort) => void;
  className?: string;
}) {
  const active = !!sortKey && sort?.key === sortKey;
  return (
    <th
      aria-sort={active ? (sort.dir === "asc" ? "ascending" : "descending") : undefined}
      className={cn("px-3 text-left font-normal", className)}
    >
      {sortKey && onSortChange ? (
        <button
          type="button"
          onClick={() => onSortChange({ key: sortKey, dir: active && sort.dir === "asc" ? "desc" : "asc" })}
          className={cn(
            "inline-flex cursor-pointer items-center gap-1 rounded-xs text-label-md whitespace-nowrap",
            active ? "text-on-surface" : "text-on-surface-variant hover:text-on-surface",
          )}
        >
          {label}
          {active && <Icon name={sort.dir === "asc" ? "arrow_upward" : "arrow_downward"} size={16} />}
        </button>
      ) : (
        <span className="text-label-md whitespace-nowrap text-on-surface-variant">{label}</span>
      )}
    </th>
  );
}
