import { formatFileDate } from "@dokument/core";
import { useId, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { cn } from "../lib/cn";
import { isFolder, isPaged, type FileItem } from "../lib/files";
import { useFileSelection } from "../lib/useFileSelection";
import { useCoarsePointer } from "../lib/usePointer";
import { IconButton } from "./Button";
import { FileFlags } from "./FileFlags";
import { FileTypeIcon } from "./FileTypeIcon";
import { Icon } from "./Icon";
import { ActionContextMenu, ActionMenu, type MenuAction } from "./Menu";

export type FileViewProps<T extends FileItem = FileItem> = {
  /** Shown in this order, so sort before passing them in. Folders are grouped first. */
  items: T[];
  selected: string[];
  onSelectedChange: (ids: string[]) => void;
  onOpen?: (item: T) => void;
  onTrash?: (ids: string[]) => void;
  /** The right-click and "More actions" menu for one item. */
  menuActions?: (item: T) => MenuAction[];
  onAction?: (action: string, item: T) => void;
  /** Overrides the metadata line, such as "You opened today" in Recent. */
  meta?: (item: T) => ReactNode;
};

type TileProps = {
  item: FileItem;
  selected: boolean;
  itemProps: ReturnType<ReturnType<typeof useFileSelection<FileItem>>["itemProps"]>;
  menuActions?: MenuAction[];
  onAction?: (action: string) => void;
  meta: ReactNode;
};

function Tile({ item, selected, itemProps, menuActions, onAction, meta }: TileProps) {
  const { t } = useTranslation();
  const coarse = useCoarsePointer();
  const [loaded, setLoaded] = useState(false);
  const folder = isFolder(item);
  const more = menuActions && onAction && (
    <ActionMenu
      actions={menuActions}
      onAction={onAction}
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
  );

  const tile = (
    <div role="row" className="contents">
      <div
        role="gridcell"
        {...itemProps}
        className={cn(
          "group relative flex min-w-0 cursor-default rounded-md border text-left transition-[box-shadow,background-color] duration-(--duration-short) select-none [-webkit-touch-callout:none]",
          selected
            ? "border-transparent bg-primary-container text-on-primary-container"
            : "border-outline-variant bg-surface-container-lowest text-on-surface hover:shadow-elevation-1",
          folder ? "h-14 items-center gap-3 pr-1 pl-3" : "flex-col gap-2 p-2",
          item.arrived && "animate-arrive",
        )}
      >
        {folder ? (
          <>
            <FileTypeIcon kind="folder" shared={item.shared} />
            <span title={item.name} className="min-w-0 flex-1 truncate font-medium">
              {item.name}
            </span>
            <FileFlags item={item} />
            {more}
          </>
        ) : (
          <>
            <div className="flex min-w-0 items-center gap-2 pl-1">
              <FileTypeIcon kind={item.kind} size="sm" />
              <span title={item.name} className="min-w-0 flex-1 truncate font-medium">
                {item.name}
              </span>
              {more}
            </div>
            <div
              className={cn(
                "relative flex aspect-4/3 items-center justify-center overflow-hidden rounded-sm",
                selected ? "bg-surface-container-lowest" : "bg-surface-container-high",
              )}
            >
              {item.thumbnail ? (
                <img
                  src={item.thumbnail}
                  alt=""
                  draggable={false}
                  onLoad={() => setLoaded(true)}
                  className={cn(
                    "size-full object-cover transition-opacity duration-(--duration-medium)",
                    isPaged(item) && "object-top",
                    loaded ? "opacity-100" : "opacity-0",
                  )}
                />
              ) : (
                <FileTypeIcon kind={item.kind} size={56} />
              )}
              {item.thumbnailPending && (
                <span
                  aria-hidden
                  className="absolute inset-0 animate-shimmer bg-linear-100 from-transparent from-30% via-surface-container-lowest/60 to-transparent to-70%"
                />
              )}
              {item.duration && (
                <span className="tabular absolute right-2 bottom-2 rounded-xs bg-scrim px-1 text-label-sm text-white">
                  {item.duration}
                </span>
              )}
              {selected && (
                <span className="absolute top-2 left-2 inline-flex rounded-full bg-surface-container-lowest text-primary">
                  <Icon name="check_circle" filled />
                </span>
              )}
            </div>
            <div
              className={cn(
                "flex min-w-0 items-center gap-1 px-1 pb-1 text-body-sm",
                selected ? "text-on-primary-container" : "text-on-surface-variant",
              )}
            >
              <span className="tabular min-w-0 flex-1 truncate">
                {item.thumbnailPending ? t("files.previewPending") : meta}
              </span>
              <FileFlags item={item} />
            </div>
          </>
        )}
      </div>
    </div>
  );

  if (!menuActions || !onAction) return tile;
  return (
    <ActionContextMenu actions={menuActions} onAction={onAction} disabled={coarse}>
      {tile}
    </ActionContextMenu>
  );
}

/**
 * Folders first, then file tiles with 4:3 thumbnails (§16.4). A shimmer shows while a thumbnail
 * is pending, and video tiles show their duration.
 */
export function FileGrid<T extends FileItem>({
  items,
  selected,
  onSelectedChange,
  onOpen,
  onTrash,
  menuActions,
  onAction,
  meta,
}: FileViewProps<T>) {
  const { t, i18n } = useTranslation();
  const id = useId();
  const folders = items.filter(isFolder);
  const files = items.filter((item) => !isFolder(item));
  const ordered = [...folders, ...files];
  const { containerRef, itemProps } = useFileSelection({
    items: ordered,
    selected,
    onSelectedChange,
    onOpen,
    onTrash,
  });

  const section = (list: T[], heading: string, headingId: string) =>
    list.length > 0 && (
      <section className="flex flex-col gap-3">
        <h2 id={headingId} className="m-0 text-title-sm text-on-surface">
          {heading}
        </h2>
        <div
          role="grid"
          aria-labelledby={headingId}
          aria-multiselectable
          className="grid grid-cols-2 gap-3 sm:grid-cols-[repeat(auto-fill,minmax(var(--spacing-tile-min),1fr))] sm:gap-4"
        >
          {list.map((item) => (
            <Tile
              key={item.id}
              item={item}
              selected={selected.includes(item.id)}
              itemProps={itemProps(item)}
              menuActions={menuActions?.(item)}
              onAction={onAction && ((action) => onAction(action, item))}
              meta={
                meta?.(item) ??
                t("files.modifiedOn", { date: formatFileDate(item.modified, i18n.language, { inSentence: true }) })
              }
            />
          ))}
        </div>
      </section>
    );

  return (
    <div ref={containerRef} className="flex flex-col gap-6">
      {section(folders, t("files.folders"), `${id}-folders`)}
      {section(files, t("files.files"), `${id}-files`)}
    </div>
  );
}
