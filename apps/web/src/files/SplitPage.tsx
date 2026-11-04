import { pageSheetUrl, type PageSheets } from "@dokument/api";
import { everyCuts, parsePages, partsFromCuts, rangesText, stemOf, withPdfExtension, type PageList, type PagesProblem } from "@dokument/core";
import { ActionMenu, Banner, Button, Chip, EmptyState, Icon, IconButton, Switch, TextField } from "@dokument/ui";
import { useQuery } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useEffect, useState, type CSSProperties } from "react";
import { useTranslation } from "react-i18next";
import { errorMessage, nodeQuery } from "./drive";
import { pageSheetsQuery, pdf, usePdfTasks } from "./pdf";

/** The most parts one split makes, as the server says (§8.4). */
const maxParts = 500;

type Mode = "pages" | "ranges";
type RangePart = { key: number; pages: string; name: string };

/** Tiles are narrower on phones, so three fit across. */
function useTileWidth(): number {
  const query = "(max-width: 599px)";
  const [small, setSmall] = useState(() => typeof window !== "undefined" && window.matchMedia(query).matches);
  useEffect(() => {
    const media = window.matchMedia(query);
    const change = () => setSmall(media.matches);
    media.addEventListener("change", change);
    return () => media.removeEventListener("change", change);
  }, []);
  return small ? 88 : 112;
}

/** Where page `page` (from 1) is on its sheet, as a background for a tile `width` wide. */
function tileStyle(id: string, sheets: PageSheets, page: number, width: number): CSSProperties {
  const index = page - 1;
  const sheet = Math.floor(index / sheets.perSheet) + 1;
  const cell = index % sheets.perSheet;
  const onSheet = Math.min(sheets.perSheet, sheets.pageCount - (sheet - 1) * sheets.perSheet);
  const across = Math.min(sheets.columns, onSheet);
  const rows = Math.ceil(onSheet / across);
  const height = (sheets.cellHeight / sheets.cellWidth) * width;
  return {
    backgroundImage: `url(${pageSheetUrl(id, sheet)})`,
    backgroundSize: `${across * width}px ${rows * height}px`,
    backgroundPosition: `${-(cell % across) * width}px ${-Math.floor(cell / across) * height}px`,
  };
}

/**
 * Splits a PDF into new files (§8.4): the person cuts between pages, or picks a preset, can leave pages out, names
 * each part, and chooses where they go. In Ranges they type each part's pages instead, which may overlap.
 */
export function SplitPage({ fileId, from }: { fileId: string; from?: "viewer" }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const tasks = usePdfTasks();
  const details = useQuery(nodeQuery(fileId));
  const node = details.data?.node;
  const sheets = useQuery({ ...pageSheetsQuery(fileId, node?.currentVersionId ?? null), enabled: !!node });
  const pageCount = sheets.data?.pageCount ?? node?.pdf?.pageCount ?? 0;
  const width = useTileWidth();
  const stem = stemOf(node?.name ?? "");

  const [mode, setMode] = useState<Mode>("pages");
  const [cuts, setCuts] = useState<Set<number>>(() => new Set());
  const [left, setLeft] = useState<Set<number>>(() => new Set());
  const [ranges, setRanges] = useState<RangePart[]>([]);
  const [names, setNames] = useState<Record<string, string>>({});
  const [newFolder, setNewFolder] = useState(false);
  const [folderName, setFolderName] = useState<string | null>(null);
  const [trash, setTrash] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const defaultName = (pages: PageList) =>
    t("pdf.split.partName", { stem, pages: rangesText(pages, "–"), count: pages.length });
  const pagesProblem = (problem: PagesProblem) => {
    switch (problem.kind) {
      case "empty":
        return t("pdf.split.problems.empty");
      case "invalid":
        return t("pdf.split.problems.invalid", { text: problem.text });
      case "backwards":
        return t("pdf.split.problems.backwards", { text: problem.text });
      case "outside":
        return t("pdf.split.problems.outside", { text: problem.text, count: problem.pageCount });
    }
  };

  // The parts as they'd be made: from the cuts, or from what's typed in Ranges.
  const parts =
    mode === "pages"
      ? partsFromCuts(pageCount, cuts, left).map((pages) => {
          const key = rangesText(pages);
          return { key, pages, name: names[key] ?? defaultName(pages), problem: undefined as string | undefined };
        })
      : ranges.map((part) => {
          const read = parsePages(part.pages, pageCount);
          return "pages" in read
            ? { key: String(part.key), pages: read.pages, name: part.name || defaultName(read.pages), problem: undefined }
            : { key: String(part.key), pages: [] as PageList, name: part.name, problem: pagesProblem(read.problem) };
        });
  const inPart = new Map<number, number>();
  parts.forEach((part, index) => part.pages.forEach((page) => inPart.has(page) || inPart.set(page, index)));
  // One part with every page would just be a copy.
  const ready =
    (parts.length > 1 || left.size > 0 || mode === "ranges") &&
    parts.length > 0 &&
    parts.length <= maxParts &&
    parts.every((part) => !part.problem && part.name.trim()) &&
    (!newFolder || !!(folderName ?? stem).trim());

  const close = () =>
    void (from === "viewer"
      ? navigate({ to: "/files/$fileId", params: { fileId } })
      : node?.parentId
        ? navigate({ to: "/folders/$folderId", params: { folderId: node.parentId } })
        : navigate({ to: "/" }));

  const toggle = (set: Set<number>, page: number) => {
    const next = new Set(set);
    if (!next.delete(page)) next.add(page);
    return next;
  };

  const toRanges = () => {
    setRanges(
      parts.map((part, index) => ({ key: index, pages: rangesText(part.pages), name: names[part.key] ?? "" })),
    );
    setMode("ranges");
  };

  const submit = async () => {
    if (!node || !ready) return;
    setBusy(true);
    setError(null);
    try {
      const op = await pdf.split({
        id: node.id,
        parts: parts.map((part) => ({ name: withPdfExtension(part.name), pages: rangesText(part.pages) })),
        newFolderName: newFolder ? (folderName ?? stem).trim() : null,
        trashOriginal: trash,
      });
      tasks.started(op);
      void (node.parentId
        ? navigate({ to: "/folders/$folderId", params: { folderId: node.parentId } })
        : navigate({ to: "/" }));
    } catch (problem) {
      setError(errorMessage(t, problem));
      setBusy(false);
    }
  };

  if (details.isError || sheets.isError) {
    return (
      <div className="fixed inset-0 z-40 flex items-center justify-center bg-surface">
        <EmptyState
          icon="error"
          title={details.isError ? errorMessage(t, details.error) : t("pdf.split.unreadable")}
          action={<Button onClick={close}>{t("common.close")}</Button>}
          className="m-4 max-w-md"
        />
      </div>
    );
  }

  const height = Math.round((283 / 200) * width);
  const tile = (page: number) => {
    const part = inPart.get(page);
    const out = part === undefined;
    const cut = cuts.has(page);
    const menu = (
      <ActionMenu<"cut" | "leave">
        align="start"
        actions={[
          {
            id: "cut",
            icon: "content_cut",
            label: cut ? t("pdf.split.joinAfter") : t("pdf.split.cutAfter"),
            disabled: page === pageCount,
          },
          { id: "leave", icon: out ? "add_circle" : "remove_circle", label: out ? t("pdf.split.putBack") : t("pdf.split.leaveOut") },
        ]}
        onAction={(action) => (action === "cut" ? setCuts((set) => toggle(set, page)) : setLeft((set) => toggle(set, page)))}
        trigger={
          <button
            type="button"
            disabled={mode === "ranges"}
            aria-label={t("pdf.split.pageMenu", { page })}
            className="group relative block cursor-pointer rounded-xs bg-surface-container-lowest text-left shadow-elevation-1 outline-offset-2 disabled:cursor-default"
            style={{ width, height }}
          >
            {sheets.data?.ready ? (
              <span aria-hidden className="absolute inset-0 rounded-xs bg-no-repeat" style={tileStyle(fileId, sheets.data, page, width)} />
            ) : (
              <span aria-hidden className="absolute inset-0 animate-pulse rounded-xs bg-surface-container-high" />
            )}
            {out ? (
              <span className="absolute inset-0 flex items-center justify-center rounded-xs bg-surface/70 text-on-surface-variant">
                <Icon name="block" />
              </span>
            ) : (
              <span
                className={`tabular absolute top-1 left-1 min-w-6 rounded-full px-1.5 text-center text-label-sm ${part % 2 === 0 ? "bg-primary text-on-primary" : "bg-secondary-container text-on-secondary-container"}`}
              >
                {part + 1}
              </span>
            )}
          </button>
        }
      />
    );
    return (
      <div key={page} className="flex items-stretch">
        <figure className="m-0 flex flex-col items-center gap-1">
          {menu}
          <figcaption className={`tabular text-body-sm ${out ? "text-on-surface-variant line-through" : "text-on-surface-variant"}`}>
            {page}
          </figcaption>
        </figure>
        {page < pageCount &&
          (mode === "pages" ? (
            <button
              type="button"
              aria-pressed={cut}
              aria-label={cut ? t("pdf.split.joinAt", { page, next: page + 1 }) : t("pdf.split.cutAt", { page, next: page + 1 })}
              title={cut ? t("pdf.split.joinAfter") : t("pdf.split.cutAfter")}
              onClick={() => setCuts((set) => toggle(set, page))}
              className="group/cut flex w-6 cursor-pointer flex-col items-center justify-start text-primary max-sm:w-3 max-sm:pointer-events-none"
              style={{ height }}
            >
              <span className={`w-0.5 flex-1 rounded-full ${cut ? "bg-primary" : "bg-transparent group-hover/cut:bg-outline"}`} />
              <Icon
                name="content_cut"
                size={18}
                className={`max-sm:hidden ${cut ? "" : "opacity-0 group-hover/cut:opacity-100 group-focus-visible/cut:opacity-100"}`}
              />
              <span className={`w-0.5 flex-1 rounded-full ${cut ? "bg-primary" : "bg-transparent group-hover/cut:bg-outline"}`} />
            </button>
          ) : (
            <span className="w-6 max-sm:w-3" />
          ))}
      </div>
    );
  };

  return (
    <div className="fixed inset-0 z-40 flex flex-col bg-surface text-on-surface">
      <header className="flex h-16 shrink-0 items-center gap-2 bg-surface-container-low px-2 sm:px-4">
        <IconButton icon="close" label={t("common.close")} onClick={close} />
        <h1 className="m-0 min-w-0 flex-1 truncate text-title-lg" title={node?.name}>
          {node ? t("pdf.split.title", { name: node.name }) : ""}
        </h1>
        <Button onClick={() => void submit()} disabled={busy || !ready}>
          {t("pdf.split.submit", { count: parts.length })}
        </Button>
      </header>
      <div className="flex min-h-0 flex-1 max-lg:flex-col max-lg:overflow-y-auto">
        <main className="min-w-0 flex-1 px-4 py-4 sm:px-6 lg:overflow-y-auto">
          <div className="mb-4 flex flex-wrap items-center gap-2">
            <Chip selected={mode === "pages"} onClick={() => setMode("pages")}>
              {t("pdf.split.modes.pages")}
            </Chip>
            <Chip selected={mode === "ranges"} onClick={toRanges}>
              {t("pdf.split.modes.ranges")}
            </Chip>
            {mode === "pages" && (
              <>
                <span aria-hidden className="mx-1 h-6 w-px bg-outline-variant" />
                <Button variant="text" onClick={() => setCuts(everyCuts(pageCount, 1))}>
                  {t("pdf.split.everyPage")}
                </Button>
                <Button variant="text" onClick={() => setCuts(everyCuts(pageCount, 2))}>
                  {t("pdf.split.everyTwo")}
                </Button>
                {(cuts.size > 0 || left.size > 0) && (
                  <Button
                    variant="text"
                    onClick={() => {
                      setCuts(new Set());
                      setLeft(new Set());
                    }}
                  >
                    {t("pdf.split.startOver")}
                  </Button>
                )}
              </>
            )}
          </div>
          <p className="m-0 mb-4 text-body-md text-on-surface-variant">
            {mode === "pages" ? t("pdf.split.pagesHint") : t("pdf.split.rangesHint")}
          </p>
          {sheets.data && !sheets.data.ready && (
            <p className="m-0 mb-4 flex items-center gap-2 text-body-md text-on-surface-variant">
              <Icon name="hourglass_top" size={18} />
              {t("pdf.split.preparing")}
            </p>
          )}
          <div className="flex flex-wrap gap-y-3">{Array.from({ length: pageCount }, (_, i) => tile(i + 1))}</div>
        </main>
        <aside className="flex shrink-0 flex-col gap-4 bg-surface-container-low px-4 py-4 sm:px-6 lg:w-100 lg:overflow-y-auto">
          {error && <Banner tone="error">{error}</Banner>}
          <h2 className="m-0 text-title-md">{t("pdf.split.parts", { count: parts.length })}</h2>
          {parts.length > maxParts && <Banner tone="warning">{t("pdf.split.tooMany", { count: maxParts })}</Banner>}
          {parts.length === 0 && mode === "pages" && (
            <p className="m-0 text-body-md text-on-surface-variant">{t("pdf.split.nothingLeft")}</p>
          )}
          <ol className="m-0 flex list-none flex-col gap-3 p-0">
            {parts.slice(0, maxParts).map((part, index) => (
              <li key={part.key} className="flex items-start gap-3">
                <span
                  className={`tabular mt-4 min-w-6 rounded-full px-1.5 text-center text-label-sm ${index % 2 === 0 ? "bg-primary text-on-primary" : "bg-secondary-container text-on-secondary-container"}`}
                >
                  {index + 1}
                </span>
                <div className="flex min-w-0 flex-1 flex-col gap-2">
                  <TextField
                    label={t("pdf.split.name")}
                    value={part.name}
                    maxLength={255}
                    onChange={(event) => {
                      const value = event.target.value;
                      if (mode === "pages") setNames((all) => ({ ...all, [part.key]: value }));
                      else setRanges((all) => all.map((r) => (String(r.key) === part.key ? { ...r, name: value } : r)));
                    }}
                  />
                  {mode === "ranges" ? (
                    <TextField
                      label={t("pdf.split.pages")}
                      value={ranges.find((r) => String(r.key) === part.key)?.pages ?? ""}
                      error={part.problem}
                      hint={t("pdf.split.pagesFieldHint", { count: pageCount })}
                      inputMode="numeric"
                      onChange={(event) => {
                        const value = event.target.value;
                        setRanges((all) => all.map((r) => (String(r.key) === part.key ? { ...r, pages: value } : r)));
                      }}
                    />
                  ) : (
                    <span className="tabular text-body-sm text-on-surface-variant">
                      {t("pdf.split.partPages", { pages: rangesText(part.pages, "–"), count: part.pages.length })}
                    </span>
                  )}
                </div>
                {mode === "ranges" && (
                  <IconButton
                    icon="delete"
                    label={t("pdf.split.removePart", { number: index + 1 })}
                    disabled={ranges.length <= 1}
                    onClick={() => setRanges((all) => all.filter((r) => String(r.key) !== part.key))}
                    className="mt-2"
                  />
                )}
              </li>
            ))}
          </ol>
          {mode === "ranges" && (
            <Button
              variant="outlined"
              icon="add"
              onClick={() =>
                setRanges((all) => [...all, { key: Math.max(-1, ...all.map((r) => r.key)) + 1, pages: "", name: "" }])
              }
            >
              {t("pdf.split.addPart")}
            </Button>
          )}
          <hr className="m-0 border-outline-variant" />
          <label className="flex cursor-pointer items-center gap-4">
            <span className="flex-1 text-body-lg">{t("pdf.split.newFolder")}</span>
            <Switch checked={newFolder} onCheckedChange={setNewFolder} />
          </label>
          {newFolder && (
            <TextField
              label={t("pdf.split.folderName")}
              value={folderName ?? stem}
              maxLength={255}
              onChange={(event) => setFolderName(event.target.value)}
            />
          )}
          <label className="flex cursor-pointer items-center gap-4">
            <span className="flex-1 text-body-lg">{t("pdf.split.trash")}</span>
            <Switch checked={trash} onCheckedChange={setTrash} />
          </label>
          <p className="m-0 flex items-start gap-2 text-body-sm text-on-surface-variant">
            <Icon name="info" size={18} />
            <span>{t("pdf.notKept")}</span>
          </p>
        </aside>
      </div>
    </div>
  );
}
