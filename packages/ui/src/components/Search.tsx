import { useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { cn } from "../lib/cn";
import { useFormat } from "../lib/display";
import { contentsLabel, isFolder, isPaged, type FileItem } from "../lib/files";
import { FileTypeIcon } from "./FileTypeIcon";
import { Highlighted } from "./Highlighted";
import { Icon } from "./Icon";
import { formatMs } from "../lib/media";
import { TagStrip, type TagSuggestionItem } from "./Tags";

/** Where the best hit in a file was, as the server says. */
export type HitSource = "name" | "text" | "ocr" | "metadata" | "tag" | "transcript";

export type SearchResultProps = {
  item: FileItem;
  /** "My files / Kvitton / 2025". */
  location: string;
  /** The search terms, for marking the name. */
  terms: readonly string[];
  snippet?: {
    text: string;
    highlights: readonly (readonly number[])[];
    cutStart: boolean;
    cutEnd: boolean;
  } | null;
  source: HitSource;
  pageNo?: number | null;
  pageCount?: number | null;
  pagesWithHits?: number;
  /** For a hit in what's said in a recording: where it is, in milliseconds. */
  atMs?: number | null;
  /** A link to the file, so it opens in a new tab with a middle click. */
  href: string;
  onOpen: () => void;
};

/**
 * One row of search results (docs/design.md §16.4): a thumbnail, the name with its hits marked, the best snippet,
 * then where the file is, when it changed, which page matched and whether the text came from a scan.
 */
export function SearchResult({
  item,
  location,
  terms,
  snippet,
  source,
  pageNo,
  pageCount,
  pagesWithHits = 0,
  atMs,
  href,
  onOpen,
}: SearchResultProps) {
  const { t } = useTranslation();
  const format = useFormat();
  const page =
    pageNo && pageCount
      ? t(item.kind === "slides" ? "search.slide" : "search.page", {
          page: pageNo,
          total: pageCount,
        })
      : pageNo && item.kind === "pdf"
        ? t("search.pageOnly", { page: pageNo })
        : null;
  const badge =
    source === "ocr"
      ? {
          icon: "document_scanner",
          label: item.kind === "image" ? t("search.foundInPhoto") : t("search.foundInScan"),
        }
      : source === "metadata"
        ? { icon: "info", label: t("search.foundInDetails") }
        : source === "tag"
          ? { icon: "sell", label: t("search.foundInTags") }
        : source === "transcript"
          ? { icon: "graphic_eq", label: t("search.foundInSpeech") }
          : null;
  return (
    <a
      href={href}
      onClick={(event) => {
        if (event.metaKey || event.ctrlKey || event.shiftKey || event.button !== 0) return;
        event.preventDefault();
        onOpen();
      }}
      className="flex w-full items-start gap-4 rounded-md border border-outline-variant bg-surface-container-lowest p-3 text-on-surface no-underline hover:shadow-elevation-1 focus-visible:outline-2 max-sm:gap-3 max-sm:p-2"
    >
      <ResultThumbnail item={item} />
      <span className="flex min-w-0 flex-1 flex-col gap-1">
        <span className="flex min-w-0 items-center gap-2">
          <FileTypeIcon kind={item.kind} size="sm" className="max-sm:hidden" />
          <span className="truncate text-title-sm">
            <Highlighted text={item.name} terms={terms} />
          </span>
        </span>
        {snippet && snippet.text && (
          <span className="line-clamp-2 text-body-md break-words text-on-surface-variant">
            {snippet.cutStart && "… "}
            <Highlighted text={snippet.text} ranges={snippet.highlights} />
            {snippet.cutEnd && " …"}
          </span>
        )}
        <span className="tabular flex flex-wrap items-center gap-x-2 gap-y-1 text-body-sm text-on-surface-variant">
          <span className="min-w-0 truncate">{location}</span>
          <Dot />
          <span>
            {isFolder(item) && item.contents
              ? contentsLabel(t, item.contents)
              : format.date(item.modified)}
          </span>
          {atMs != null ? (
            <>
              <Dot />
              <span className="inline-flex items-center gap-0.5 font-medium text-primary">
                <Icon name="play_arrow" filled size={16} />
                {t("search.at", { time: formatMs(atMs) })}
              </span>
            </>
          ) : (
            page && (
              <>
                <Dot />
                <span>{page}</span>
              </>
            )
          )}
          {pagesWithHits > 1 && atMs == null && (
            <>
              <Dot />
              <span>{t("search.pagesWithHits", { count: pagesWithHits })}</span>
            </>
          )}
          {badge && (
            <span className="inline-flex items-center gap-1 rounded-xs bg-secondary-container px-1.5 text-label-sm text-on-secondary-container">
              <Icon name={badge.icon} size={14} />
              {badge.label}
            </span>
          )}
        </span>
        {item.tags && item.tags.length > 0 && (
          <TagStrip tags={item.tags} max={4} className="flex-wrap" />
        )}
      </span>
    </a>
  );
}

const Dot = () => <span aria-hidden>·</span>;

function ResultThumbnail({ item }: { item: FileItem }) {
  const [failed, setFailed] = useState(false);
  const box =
    "flex h-18 w-24 shrink-0 items-center justify-center overflow-hidden rounded-sm bg-surface-container-high max-sm:h-12 max-sm:w-16";
  if (isFolder(item) || !item.thumbnail || failed) {
    return (
      <span className={box}>
        <FileTypeIcon kind={item.kind} shared={item.shared} size={32} />
      </span>
    );
  }
  return (
    <span
      className={cn(
        box,
        isPaged(item) && "border border-outline-variant bg-surface-container-lowest",
      )}
    >
      <img
        src={item.thumbnail}
        alt=""
        loading="lazy"
        draggable={false}
        onError={() => setFailed(true)}
        className={cn("size-full object-cover", isPaged(item) && "object-top")}
      />
    </span>
  );
}

export type SearchSuggestion = { item: FileItem; location: string };

export type SearchSuggestionsProps = {
  id: string;
  query: string;
  terms: readonly string[];
  suggestions: SearchSuggestion[];
  /** Tags that start with what was typed, listed above the names (§13.4). */
  tags?: TagSuggestionItem[];
  onPickTag?: (name: string) => void;
  /** The highlighted option: -1 is "Search for …", then the tags, then the names, in order. */
  active: number;
  onActiveChange: (index: number) => void;
  onSearch: () => void;
  onPick: (item: FileItem) => void;
  /** The "Narrow it down" filters, as chips. */
  filters?: ReactNode;
  className?: string;
};

/** The list under the search bar while someone types (§16.4): search everything, or jump to a name that matches. */
export function SearchSuggestions({
  id,
  query,
  terms,
  suggestions,
  tags = [],
  onPickTag,
  active,
  onActiveChange,
  onSearch,
  onPick,
  filters,
  className,
}: SearchSuggestionsProps) {
  const { t } = useTranslation();
  const option =
    "flex min-h-12 cursor-pointer items-center gap-3 px-4 text-body-md pointer-coarse:min-h-touch";
  return (
    <div
      className={cn(
        "z-30 overflow-hidden bg-surface-container-high pb-2 text-on-surface shadow-elevation-2",
        className,
      )}
    >
      <div
        role="listbox"
        id={id}
        aria-label={t("search.suggestions")}
        className="border-t border-outline-variant"
      >
        <div
          role="option"
          id={`${id}-search`}
          aria-selected={active === -1}
          onPointerDown={(event) => event.preventDefault()}
          onPointerEnter={() => onActiveChange(-1)}
          onClick={onSearch}
          className={cn(option, active === -1 && "bg-on-surface/8")}
        >
          <Icon name="search" className="text-on-surface-variant" />
          <span className="min-w-0 flex-1 truncate">{t("search.searchFor", { query })}</span>
          <kbd className="rounded-xs border border-outline-variant px-1.5 font-sans text-label-sm text-on-surface-variant max-sm:hidden">
            Enter
          </kbd>
        </div>
        {tags.length > 0 && (
          <div role="presentation" className="px-4 pt-2 pb-1 text-label-md text-on-surface-variant">
            {t("search.tagsHeading")}
          </div>
        )}
        {tags.map((tag, index) => (
          <div
            key={tag.name}
            role="option"
            id={`${id}-${index}`}
            aria-selected={active === index}
            onPointerDown={(event) => event.preventDefault()}
            onPointerEnter={() => onActiveChange(index)}
            onClick={() => onPickTag?.(tag.name)}
            className={cn(option, active === index && "bg-on-surface/8")}
          >
            <Icon name="sell" className="text-on-surface-variant" />
            <span className="min-w-0 flex-1 truncate">
              <Highlighted text={tag.name} terms={terms} />
            </span>
            <span className="tabular shrink-0 text-body-sm text-on-surface-variant">
              {t("tags.files", { count: tag.files })}
            </span>
          </div>
        ))}
        {suggestions.length > 0 && (
          <div role="presentation" className="px-4 pt-2 pb-1 text-label-md text-on-surface-variant">
            {t("search.names")}
          </div>
        )}
        {suggestions.map(({ item, location }, i) => {
          const index = tags.length + i;
          return (
            <div
              key={item.id}
              role="option"
              id={`${id}-${index}`}
              aria-selected={active === index}
              onPointerDown={(event) => event.preventDefault()}
              onPointerEnter={() => onActiveChange(index)}
              onClick={() => onPick(item)}
              className={cn(option, active === index && "bg-on-surface/8")}
            >
              <FileTypeIcon kind={item.kind} shared={item.shared} />
              <span className="min-w-0 flex-1 truncate">
                <Highlighted text={item.name} terms={terms} />
              </span>
              <span className="max-w-[40%] shrink-0 truncate text-body-sm text-on-surface-variant">
                {location}
              </span>
            </div>
          );
        })}
      </div>
      {filters && (
        <div
          onPointerDown={(event) => event.preventDefault()}
          className="mt-2 flex flex-wrap items-center gap-2 border-t border-outline-variant px-4 pt-3 text-body-sm text-on-surface-variant"
        >
          <span>{t("search.narrow")}</span>
          {filters}
          <span className="ml-auto font-mono text-label-sm max-md:hidden">{t("search.tip")}</span>
        </div>
      )}
    </div>
  );
}
