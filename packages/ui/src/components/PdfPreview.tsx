import { findHits } from "@dokument/core";
import { getDocument, GlobalWorkerOptions, type PDFDocumentProxy, type PDFPageProxy, type PageViewport, type RenderTask } from "pdfjs-dist";
import workerUrl from "pdfjs-dist/build/pdf.worker.min.mjs?url";
import { useEffect, useRef, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { cn } from "../lib/cn";
import { IconButton } from "./Button";
import { Loading, MarkOverlay, ToolbarDivider, ViewerToolbar, ZoomControls, type MarkBox } from "./FileViewer";

GlobalWorkerOptions.workerSrc = workerUrl;

/**
 * PDFs in the viewer (docs/design.md §14), with pdf.js. It reads the file with range requests, so a long PDF opens at
 * page 1 without downloading the rest. One page at a time, fitted to the window's width; Page Up and Page Down turn
 * pages, since the arrow keys move between files. Loaded only when a PDF is opened.
 *
 * Opened from search, it marks the hits: in the page's text layer by the search rule, or, for a scanned page, at the
 * boxes the server recognized.
 */
export default function PdfPreview({
  src,
  page,
  onPageChange: setPage,
  terms,
  boxes,
  fallback,
}: {
  src: string;
  page: number;
  onPageChange: (page: number) => void;
  terms?: readonly string[];
  boxes: readonly MarkBox[];
  fallback: ReactNode;
}) {
  const { t } = useTranslation();
  const [document, setDocument] = useState<PDFDocumentProxy | "failed" | null>(null);
  const [marks, setMarks] = useState<readonly MarkBox[]>([]);
  const [zoom, setZoom] = useState(1);
  const [rendering, setRendering] = useState(true);
  const box = useRef<HTMLDivElement>(null);
  const canvas = useRef<HTMLCanvasElement>(null);
  const [width, setWidth] = useState(0);

  useEffect(() => {
    const task = getDocument({ url: src, withCredentials: true });
    task.promise.then(setDocument, () => setDocument("failed"));
    return () => void task.destroy();
  }, [src]);

  useEffect(() => {
    const element = box.current;
    if (!element) return;
    const observer = new ResizeObserver(([entry]) => entry && setWidth(entry.contentRect.width));
    observer.observe(element);
    return () => observer.disconnect();
  }, [document]);

  useEffect(() => {
    if (!document || document === "failed" || !canvas.current || width === 0) return;
    let task: RenderTask | undefined;
    let cancelled = false;
    setRendering(true);
    void document.getPage(page).then((pdfPage) => {
      if (cancelled || !canvas.current) return;
      const natural = pdfPage.getViewport({ scale: 1 });
      // Fit the width, but never wider than an A4 page at 150 %, so text stays a readable size on big screens.
      const fitted = Math.min(width - 32, natural.width * 1.5) / natural.width;
      const viewport = pdfPage.getViewport({ scale: fitted * zoom });
      const ratio = window.devicePixelRatio || 1;
      const element = canvas.current;
      element.width = Math.floor(viewport.width * ratio);
      element.height = Math.floor(viewport.height * ratio);
      element.style.width = `${Math.floor(viewport.width)}px`;
      element.style.height = `${Math.floor(viewport.height)}px`;
      task = pdfPage.render({ canvas: element, viewport, transform: ratio === 1 ? undefined : [ratio, 0, 0, ratio, 0, 0] });
      task.promise.then(
        () => !cancelled && setRendering(false),
        () => undefined,
      );
    });
    return () => {
      cancelled = true;
      task?.cancel();
    };
  }, [document, page, zoom, width]);

  // Hits on this page: the recognized boxes for a scan, otherwise words found in the text layer.
  const boxesKey = JSON.stringify(boxes);
  const termsKey = terms?.join(" ") ?? "";
  useEffect(() => {
    if (!document || document === "failed") return;
    let cancelled = false;
    void document.getPage(page).then(async (pdfPage) => {
      const found = boxes.length > 0 ? boxes : termsKey ? await textMarks(pdfPage, termsKey.split(" ")) : [];
      if (!cancelled) setMarks(found);
    });
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- keyed by content, not identity
  }, [document, page, boxesKey, termsKey]);

  // Bring the first hit into view once the page is drawn.
  useEffect(() => {
    if (rendering || marks.length === 0) return;
    box.current?.querySelector("[data-mark]")?.scrollIntoView({ block: "center", inline: "nearest" });
  }, [rendering, marks]);

  const pages = document && document !== "failed" ? document.numPages : 0;
  const turn = (to: number) => {
    setPage(Math.min(Math.max(1, to), pages));
    box.current?.scrollTo({ top: 0 });
  };

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key === "PageDown") turn(page + 1);
      else if (event.key === "PageUp") turn(page - 1);
      else return;
      event.preventDefault();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  });

  if (document === "failed") return <div className="flex flex-1 items-center justify-center">{fallback}</div>;
  if (!document) return <Loading />;
  return (
    <>
      <div ref={box} className="flex min-h-0 flex-1 overflow-auto py-4">
        <div className="relative mx-auto shrink-0 self-start">
          <canvas
            ref={canvas}
            aria-label={t("viewer.page", { page, total: pages })}
            className={cn("block bg-white shadow-elevation-2 transition-opacity", rendering && "opacity-60")}
          />
          {!rendering && marks.length > 0 && <MarkOverlay boxes={marks} />}
        </div>
      </div>
      <ViewerToolbar>
        <IconButton icon="chevron_left" label={t("viewer.previousPage")} size="sm" disabled={page <= 1} onClick={() => turn(page - 1)} />
        <span className="tabular px-2 text-label-lg">{t("viewer.page", { page, total: pages })}</span>
        <IconButton icon="chevron_right" label={t("viewer.nextPage")} size="sm" disabled={page >= pages} onClick={() => turn(page + 1)} />
        <ToolbarDivider />
        <ZoomControls zoom={zoom} onZoom={setZoom} />
      </ViewerToolbar>
    </>
  );
}

type TextItem = { str: string; transform: number[]; width: number; height: number; fontName: string };

/**
 * Where the search hits are in a page's text layer, as fractions of the page. pdf.js gives each run of text with its
 * position and width; where a hit starts and ends within the run is measured in the run's font family, scaled to the
 * run's real width, which is close enough for marking.
 */
async function textMarks(pdfPage: PDFPageProxy, terms: string[]): Promise<MarkBox[]> {
  const viewport: PageViewport = pdfPage.getViewport({ scale: 1 });
  const content = await pdfPage.getTextContent();
  const measure = document.createElement("canvas").getContext("2d");
  const marks: MarkBox[] = [];
  for (const raw of content.items) {
    if (!("str" in raw)) continue;
    const item = raw as TextItem;
    if (!item.str) continue;
    const [a = 0, b = 0, , , x = 0, y = 0] = item.transform;
    const size = item.height || Math.hypot(a, b);
    if (measure) measure.font = `${size}px ${content.styles[item.fontName]?.fontFamily ?? "sans-serif"}`;
    const full = measure?.measureText(item.str).width || item.str.length;
    const offset = (index: number) =>
      x + (item.width * (measure ? measure.measureText(item.str.slice(0, index)).width : index)) / full;
    for (const [start, end] of findHits(item.str, terms)) {
      const left = offset(start);
      const right = offset(end);
      const [x1 = 0, y1 = 0] = viewport.convertToViewportPoint(left, y - size * 0.2) as number[];
      const [x2 = 0, y2 = 0] = viewport.convertToViewportPoint(right, y + size * 0.85) as number[];
      marks.push([
        Math.min(x1, x2) / viewport.width,
        Math.min(y1, y2) / viewport.height,
        Math.abs(x2 - x1) / viewport.width,
        Math.abs(y2 - y1) / viewport.height,
      ]);
    }
  }
  return marks;
}
