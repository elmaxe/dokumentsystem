import type { SearchResult as SearchResultDto } from "@dokument/api";
import { ActionMenu, Banner, Button, Chip, EmptyState, Icon, Progress, SearchResult } from "@dokument/ui";
import { useQuery } from "@tanstack/react-query";
import { useNavigate, useRouter } from "@tanstack/react-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { nodeQuery, pathLabel, toItem } from "../files/drive";
import {
  hasFilters,
  modifiedRanges,
  rememberResults,
  searchQuery,
  searchTypes,
  type SearchParams,
} from "./search";

const pageSize = 50;

/**
 * Search results (docs/design.md §13.3, §16.4): the filter chips mirror the query operators, results are one row per
 * file with its best snippet, and opening one shows the viewer on the page that matched.
 */
export function SearchPage({ params }: { params: SearchParams }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const router = useRouter();
  const [limit, setLimit] = useState(pageSize);
  const results = useQuery(searchQuery(params, limit));
  const folder = useQuery({ ...nodeQuery(params.folder ?? ""), enabled: !!params.folder });
  const folderName = folder.data?.node.name;
  const q = params.q ?? "";

  const update = (change: Partial<SearchParams>) =>
    void navigate({ to: "/search", search: { ...params, ...change }, replace: true });

  const viewerLink = (hit: SearchResultDto) =>
    hit.node.kind === "folder"
      ? ({ to: "/folders/$folderId", params: { folderId: hit.node.id } } as const)
      : ({
          to: "/files/$fileId",
          params: { fileId: hit.node.id },
          search: { from: "search" as const, q: q || undefined, page: hit.pageNo ?? undefined },
        } as const);

  const open = (hit: SearchResultDto) => {
    rememberResults(params, (results.data?.results ?? []).map((r) => r.node).filter((n) => n.kind === "file"));
    void navigate(viewerLink(hit));
  };

  const data = results.data;
  const heading = !data
    ? null
    : !q
      ? t("search.resultsOnly", { count: data.total })
      : folderName
        ? t("search.resultsIn", { count: data.total, query: q, folder: folderName })
        : t("search.results", { count: data.total, query: q });

  return (
    <main className="relative min-w-0 flex-1 overflow-y-auto rounded-tl-lg pb-40 md:pb-24">
      <div className="mx-auto flex w-full max-w-240 flex-col gap-4 px-4 pt-2 sm:px-6">
        <div role="toolbar" aria-label={t("search.title")} className="-mx-4 flex gap-2 overflow-x-auto px-4 sm:mx-0 sm:flex-wrap sm:px-0">
          <ActionMenu
            align="start"
            actions={searchTypes.map((type) => ({ id: type, icon: params.type === type ? "check" : "", label: t(`kinds.${type}`) }))}
            onAction={(type) => update({ type })}
            trigger={
              <span className="inline-flex">
                <Chip icon="filter_list" selected={!!params.type} onRemove={params.type ? () => update({ type: undefined }) : undefined}>
                  {params.type ? t("search.filters.type", { value: t(`kinds.${params.type}`) }) : t("search.filters.addType")}
                  {!params.type && <Icon name="arrow_drop_down" size={18} />}
                </Chip>
              </span>
            }
          />
          <ActionMenu
            align="start"
            actions={modifiedRanges.map((range) => ({
              id: range,
              icon: params.modified === range ? "check" : "",
              label: t(`search.modified.${range}`),
            }))}
            onAction={(modified) => update({ modified })}
            trigger={
              <span className="inline-flex">
                <Chip
                  selected={!!params.modified}
                  onRemove={params.modified ? () => update({ modified: undefined }) : undefined}
                >
                  {params.modified
                    ? t("search.filters.modified", { value: t(`search.modified.${params.modified}`).toLowerCase() })
                    : t("search.filters.addModified")}
                  {!params.modified && <Icon name="arrow_drop_down" size={18} />}
                </Chip>
              </span>
            }
          />
          {params.folder && (
            <Chip icon="folder" selected onRemove={() => update({ folder: undefined })}>
              {t("search.chips.inFolder", { value: folderName ?? "…" })}
            </Chip>
          )}
          <Chip icon="star" selected={!!params.starred} onClick={() => update({ starred: params.starred ? undefined : true })}>
            {t("search.chips.starred")}
          </Chip>
          {q && (
            <ActionMenu
              align="end"
              actions={(["relevance", "modified"] as const).map((sort) => ({
                id: sort,
                icon: (params.sort ?? "relevance") === sort ? "check" : "",
                label: t(`search.sort.${sort}`),
              }))}
              onAction={(sort) => update({ sort: sort === "relevance" ? undefined : sort })}
              trigger={
                <span className="inline-flex sm:ml-auto">
                  <Chip icon="sort">
                    {t(`search.sort.${params.sort ?? "relevance"}`)}
                    <Icon name="arrow_drop_down" size={18} />
                  </Chip>
                </span>
              }
            />
          )}
        </div>

        {data && data.pending > 0 && (
          <Banner>
            <span className="inline-flex items-center gap-2">{t("search.pending", { count: data.pending })}</span>
          </Banner>
        )}

        {!q && !hasFilters(params) ? (
          <EmptyState icon="search" title={t("search.startTitle")} text={t("search.startText")} className="mt-4" />
        ) : results.isError ? (
          <EmptyState icon="error" title={t("search.failed")} className="mt-4" />
        ) : !data ? (
          <Progress label={t("viewer.loading")} className="mx-auto mt-8 w-40" />
        ) : data.results.length === 0 ? (
          data.withoutFilters ? (
            <EmptyState
              icon="search_off"
              title={t("search.noFilteredTitle", { query: q })}
              text={t("search.withoutFilters", { count: data.withoutFilters })}
              action={
                <Button
                  variant="tonal"
                  onClick={() => void navigate({ to: "/search", search: { q: params.q, sort: params.sort }, replace: true })}
                >
                  {t("search.showAll", { count: data.withoutFilters })}
                </Button>
              }
              className="mt-4"
            />
          ) : (
            <EmptyState icon="search_off" title={t("search.noResultsTitle", { query: q })} text={t("search.noResultsText")} className="mt-4" />
          )
        ) : (
          <>
            <h1 className="m-0 text-title-lg" aria-live="polite">
              {heading}
            </h1>
            <ul className="m-0 flex list-none flex-col gap-2 p-0">
              {data.results.map((hit) => (
                <li key={hit.node.id}>
                  <SearchResult
                    item={toItem(hit.node)}
                    location={pathLabel(t("nav.myFiles"), hit.path)}
                    terms={data.terms}
                    snippet={hit.snippet}
                    source={hit.source}
                    pageNo={hit.pageNo}
                    pageCount={hit.pageCount}
                    pagesWithHits={hit.pagesWithHits}
                    href={router.buildLocation(viewerLink(hit)).href}
                    onOpen={() => open(hit)}
                  />
                </li>
              ))}
            </ul>
            {data.results.length < data.total && (
              <Button variant="outlined" onClick={() => setLimit(limit + pageSize)} className="self-center">
                {t("search.more")}
              </Button>
            )}
          </>
        )}
      </div>
    </main>
  );
}
