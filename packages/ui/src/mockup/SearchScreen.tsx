import { formatFileDate } from "@dokument/core";
import { Fragment, useState } from "react";
import { useTranslation } from "react-i18next";
import { Chip } from "../components/Chip";
import { EmptyState } from "../components/EmptyState";
import { FileTypeIcon } from "../components/FileTypeIcon";
import { Icon } from "../components/Icon";
import { ActionMenu } from "../components/Menu";
import { isFolder, type FileKind } from "../lib/files";
import type { Node } from "./data";
import { folderPath, useMockup } from "./store";

const typeFilters: FileKind[] = ["pdf", "image", "doc", "sheet", "video", "folder"];

/** Wraps each occurrence of the query's words in <mark>. */
function Highlight({ text, terms }: { text: string; terms: string[] }) {
  if (terms.length === 0) return <>{text}</>;
  const pattern = new RegExp(`(${terms.map((term) => term.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")).join("|")})`, "gi");
  return (
    <>
      {text.split(pattern).map((part, i) => (
        <Fragment key={i}>{i % 2 === 1 ? <mark>{part}</mark> : part}</Fragment>
      ))}
    </>
  );
}

/** Search results with filter chips that mirror the query operators (§13.3, §16.4). */
export function SearchScreen() {
  const { t, i18n } = useTranslation();
  const app = useMockup();
  const query = app.route.screen === "search" ? app.route.query : "";
  const [type, setType] = useState<FileKind | null>(null);
  const [mine, setMine] = useState(false);
  const [thisYear, setThisYear] = useState(false);

  const terms = query.toLowerCase().split(/\s+/).filter(Boolean);
  const plain = (node: Node) => (node.text ?? "").replace(/\[\[|\]\]/g, "");
  const matches = (node: Node) =>
    terms.every((term) => node.name.toLowerCase().includes(term) || plain(node).toLowerCase().includes(term));
  const results = app.nodes.filter(
    (node) =>
      node.trashedDaysLeft === undefined &&
      terms.length > 0 &&
      matches(node) &&
      (!type || node.kind === type) &&
      (!mine || node.owner === "Axel") &&
      (!thisYear || new Date(node.modified).getFullYear() === new Date().getFullYear()),
  );

  return (
    <div className="flex flex-col gap-4 pt-2">
      <div className="flex flex-wrap gap-2">
        <ActionMenu
          align="start"
          actions={typeFilters.map((kind) => ({ id: kind, icon: "check", label: t(`kinds.${kind}`) }))}
          onAction={(kind) => setType(kind)}
          trigger={
            <span>
              <Chip
                icon="filter_list"
                selected={!!type}
                onRemove={type ? () => setType(null) : undefined}
              >
                {type ? t("search.filters.type", { value: t(`kinds.${type}`) }) : t("search.filters.addType")}
              </Chip>
            </span>
          }
        />
        <Chip selected={mine} onClick={() => setMine(!mine)}>
          {mine ? t("search.filters.owner", { value: t("files.me") }) : t("search.filters.addOwner")}
        </Chip>
        <Chip selected={thisYear} onClick={() => setThisYear(!thisYear)}>
          {thisYear
            ? t("search.filters.modified", { value: t("search.filters.thisYear") })
            : t("search.filters.addModified")}
        </Chip>
      </div>

      {terms.length === 0 ? null : results.length === 0 ? (
        <EmptyState
          icon="search_off"
          title={t("search.noResultsTitle", { query })}
          text={t("search.noResultsText")}
          className="mt-4"
        />
      ) : (
        <>
          <h1 className="m-0 text-title-lg" aria-live="polite">
            {t("search.results", { count: results.length, query })}
          </h1>
          <ul className="m-0 flex list-none flex-col gap-2 p-0">
            {results.map((node) => {
              const inText = !node.name.toLowerCase().includes(terms[0] ?? "") && node.text;
              const location = [t("nav.myFiles"), ...folderPath(app.nodes, node.parentId).map((n) => n.name)].join(" / ");
              return (
                <li key={node.id}>
                  <button
                    type="button"
                    onClick={() => app.act("open", [node.id])}
                    className="flex w-full cursor-pointer items-start gap-4 rounded-md border border-outline-variant bg-surface-container-lowest p-3 text-left hover:shadow-elevation-1"
                  >
                    {node.thumbnail && !isFolder(node) ? (
                      <img src={node.thumbnail} alt="" className="h-18 w-24 shrink-0 rounded-sm object-cover max-sm:h-12 max-sm:w-16" />
                    ) : (
                      <span className="flex h-18 w-24 shrink-0 items-center justify-center rounded-sm bg-surface-container-high max-sm:h-12 max-sm:w-16">
                        <FileTypeIcon kind={node.kind} shared={node.shared} size={32} />
                      </span>
                    )}
                    <span className="flex min-w-0 flex-1 flex-col gap-1">
                      <span className="flex min-w-0 items-center gap-2">
                        <FileTypeIcon kind={node.kind} size="sm" />
                        <span className="truncate font-medium text-on-surface">
                          <Highlight text={node.name} terms={terms} />
                        </span>
                      </span>
                      {node.text && (
                        <span className="line-clamp-2 text-body-md text-on-surface-variant">
                          <Highlight text={plain(node)} terms={terms} />
                        </span>
                      )}
                      <span className="tabular flex flex-wrap items-center gap-x-2 text-body-sm text-on-surface-variant">
                        <span>{t("search.inFolder", { folder: location })}</span>
                        <span aria-hidden>·</span>
                        <span>{formatFileDate(node.modified, i18n.language)}</span>
                        {inText && (
                          <span className="inline-flex items-center gap-1 rounded-xs bg-secondary-container px-1.5 text-label-sm text-on-secondary-container">
                            <Icon name={node.ocr ? "document_scanner" : "text_snippet"} size={14} />
                            {node.ocr ? t("search.foundInScan") : t("search.foundInText")}
                          </span>
                        )}
                      </span>
                    </span>
                  </button>
                </li>
              );
            })}
          </ul>
        </>
      )}
    </div>
  );
}
