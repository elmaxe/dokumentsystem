import { formatBytes, formatFileDate } from "@dokument/core";
import { useTranslation } from "react-i18next";
import { cn } from "../lib/cn";
import type { FileItem, Sort, SortKey } from "../lib/files";
import { useFileSelection } from "../lib/useFileSelection";
import { useCoarsePointer } from "../lib/usePointer";
import { IconButton } from "./Button";
import { Checkbox } from "./Checkbox";
import { FileFlags } from "./FileFlags";
import type { FileViewProps } from "./FileGrid";
import { FileTypeIcon } from "./FileTypeIcon";
import { Icon } from "./Icon";
import { ActionContextMenu, ActionMenu } from "./Menu";

export type FileListProps<T extends FileItem = FileItem> = FileViewProps<T> & {
  sort: Sort;
  onSortChange: (sort: Sort) => void;
};

const columns: { key: SortKey; className: string }[] = [
  { key: "name", className: "" },
  { key: "owner", className: "w-[18%] @max-[600px]:hidden" },
  { key: "modified", className: "w-[18%] @max-[600px]:hidden" },
  { key: "size", className: "w-[11%] text-right @max-[600px]:hidden" },
];

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
}: FileListProps<T>) {
  const { t, i18n } = useTranslation();
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
  const size = (item: FileItem) =>
    item.size === undefined ? "—" : formatBytes(item.size, i18n.language);
  const date = (item: FileItem) => formatFileDate(item.modified, i18n.language);

  return (
    <div
      ref={containerRef}
      className="@container overflow-hidden rounded-md border border-outline-variant bg-surface-container-lowest"
    >
      <table role="grid" aria-multiselectable className="w-full table-fixed border-collapse">
        {/* Column widths live here, not on the header cells, because phones hide the header row. */}
        <colgroup>
          <col className="w-11" />
          {columns.map((column) => (
            <col key={column.key} className={column.className.replace(/\btext-right\b/, "")} />
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
            {columns.map((column) => {
              const active = sort.key === column.key;
              return (
                <th
                  key={column.key}
                  aria-sort={active ? (sort.dir === "asc" ? "ascending" : "descending") : undefined}
                  className={cn("px-3 text-left font-normal", column.className)}
                >
                  <button
                    type="button"
                    onClick={() =>
                      onSortChange({
                        key: column.key,
                        dir: active && sort.dir === "asc" ? "desc" : "asc",
                      })
                    }
                    className={cn(
                      "inline-flex cursor-pointer items-center gap-1 rounded-xs text-label-md whitespace-nowrap",
                      active ? "text-on-surface" : "text-on-surface-variant hover:text-on-surface",
                    )}
                  >
                    {t(`files.columns.${column.key}`)}
                    {active && (
                      <Icon name={sort.dir === "asc" ? "arrow_upward" : "arrow_downward"} size={16} />
                    )}
                  </button>
                </th>
              );
            })}
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
                    <FileTypeIcon
                      kind={item.kind}
                      shared={item.shared}
                      className="@max-[600px]:text-[28px]"
                    />
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
                        {meta?.(item) ?? [date(item), item.size !== undefined && size(item)]
                          .filter(Boolean)
                          .join(" · ")}
                      </span>
                    </div>
                    <FileFlags item={item} />
                  </div>
                </td>
                <td className="truncate px-3 @max-[600px]:hidden">{item.owner}</td>
                <td className="tabular truncate px-3 @max-[600px]:hidden">{date(item)}</td>
                <td className="tabular truncate px-3 text-right @max-[600px]:hidden">
                  {size(item)}
                </td>
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
