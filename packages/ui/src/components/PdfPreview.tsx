import { getDocument, GlobalWorkerOptions, type PDFDocumentProxy, type RenderTask } from "pdfjs-dist";
import workerUrl from "pdfjs-dist/build/pdf.worker.min.mjs?url";
import { useEffect, useRef, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { cn } from "../lib/cn";
import { IconButton } from "./Button";
import { Loading, ToolbarDivider, ViewerToolbar, ZoomControls } from "./FileViewer";

GlobalWorkerOptions.workerSrc = workerUrl;

/**
 * PDFs in the viewer (docs/design.md §14), with pdf.js. It reads the file with range requests, so a long PDF opens at
 * page 1 without downloading the rest. One page at a time, fitted to the window's width; Page Up and Page Down turn
 * pages, since the arrow keys move between files. Loaded only when a PDF is opened.
 */
export default function PdfPreview({ src, fallback }: { src: string; fallback: ReactNode }) {
  const { t } = useTranslation();
  const [document, setDocument] = useState<PDFDocumentProxy | "failed" | null>(null);
  const [page, setPage] = useState(1);
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
        <canvas
          ref={canvas}
          aria-label={t("viewer.page", { page, total: pages })}
          className={cn("mx-auto shrink-0 bg-white shadow-elevation-2 transition-opacity", rendering && "opacity-60")}
        />
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
