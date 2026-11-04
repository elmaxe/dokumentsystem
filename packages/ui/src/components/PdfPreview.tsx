import { findHits, type PageWord } from "@dokument/core";
import { getDocument, GlobalWorkerOptions, type PDFDocumentProxy, type PDFPageProxy, type PageViewport, type RenderTask } from "pdfjs-dist";
import workerUrl from "pdfjs-dist/build/pdf.worker.min.mjs?url";
import { useEffect, useRef, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { cn } from "../lib/cn";
import { IconButton } from "./Button";
import { Loading, MarkOverlay, ToolbarDivider, ViewerToolbar, ZoomControls, type MarkBox } from "./FileViewer";
import { NoTextNote, TextControls, TextFloatingToggle, useWords, WordLayer, type ViewerText } from "./WordLayer";

GlobalWorkerOptions.workerSrc = workerUrl;

// pdf.js fetches some data only when a file needs it: the wasm decoders for CCITT fax, JBIG2 and JPEG 2000 scans, the
// standard fonts for text without embedded fonts, and CMaps for CJK text. Without them those pages draw blank. Each
// file is bundled as its own asset and loaded on first use.
const binaryData: Record<string, Record<string, () => Promise<string>>> = {
  wasmUrl: byName(import.meta.glob<string>("../../node_modules/pdfjs-dist/wasm/*.wasm", { query: "?url", import: "default" })),
  standardFontDataUrl: byName(
    import.meta.glob<string>("../../node_modules/pdfjs-dist/standard_fonts/*.{pfb,ttf}", { query: "?url", import: "default" }),
  ),
  cMapUrl: byName(import.meta.glob<string>("../../node_modules/pdfjs-dist/cmaps/*.bcmap", { query: "?url", import: "default" })),
};

function byName<T>(files: Record<string, T>): Record<string, T> {
  return Object.fromEntries(Object.entries(files).map(([path, file]) => [path.slice(path.lastIndexOf("/") + 1), file]));
}

/** pdf.js's BinaryDataFactory, serving its data files from the bundle instead of from base URLs. */
class BundledBinaryData {
  async fetch({ kind, filename }: { kind: string; filename: string }): Promise<Uint8Array> {
    const url = binaryData[kind]?.[filename];
    if (!url) throw new Error(`pdf.js asked for ${kind} ${filename}, which isn't bundled.`);
    const response = await fetch(await url());
    if (!response.ok) throw new Error(`Couldn't load ${filename} (${response.status}).`);
    return new Uint8Array(await response.arrayBuffer());
  }
}

/**
 * PDFs in the viewer (docs/design.md §14), with pdf.js. It reads the file with range requests, so a long PDF opens at
 * page 1 without downloading the rest. One page at a time, fitted to the window's width; Page Up and Page Down turn
 * pages, since the arrow keys move between files. Loaded only when a PDF is opened.
 *
 * Opened from search, it marks the hits: in the page's text layer by the search rule, or, for a scanned page, at the
 * boxes the server recognized. The Text toggle shows a page's words, from the server for a scan and otherwise from the
 * PDF's own text, so they can be selected and copied.
 */
export default function PdfPreview({
  src,
  page,
  onPageChange: setPage,
  terms,
  boxes,
  text,
  fallback,
}: {
  src: string;
  page: number;
  onPageChange: (page: number) => void;
  terms?: readonly string[];
  boxes: readonly MarkBox[];
  text?: ViewerText;
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
    const task = getDocument({ url: src, withCredentials: true, BinaryDataFactory: BundledBinaryData });
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

  const loaded = document && document !== "failed" ? document : null;
  const words = useWords(
    text?.shown && loaded ? () => text.wordsOn(page) ?? loaded.getPage(page).then(textWords) : null,
    `${src} ${page}`,
  );

  const pages = loaded ? loaded.numPages : 0;
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
          {!rendering && words && <WordLayer words={words} />}
          {!rendering && marks.length > 0 && <MarkOverlay boxes={marks} />}
        </div>
      </div>
      {text && <TextFloatingToggle text={text} />}
      {!rendering && words?.length === 0 && <NoTextNote />}
      <ViewerToolbar>
        <IconButton icon="chevron_left" label={t("viewer.previousPage")} size="sm" disabled={page <= 1} onClick={() => turn(page - 1)} />
        <span className="tabular px-2 text-label-lg">{t("viewer.page", { page, total: pages })}</span>
        <IconButton icon="chevron_right" label={t("viewer.nextPage")} size="sm" disabled={page >= pages} onClick={() => turn(page + 1)} />
        <ToolbarDivider />
        <ZoomControls zoom={zoom} onZoom={setZoom} />
        {text && <TextControls text={text} words={words} page />}
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
  const marks: MarkBox[] = [];
  for (const run of await textRuns(pdfPage)) {
    for (const [start, end] of findHits(run.str, terms)) marks.push(run.box(start, end));
  }
  return marks;
}

/** Each word of a page's text layer with its box, for the Text toggle on pages that aren't scans. */
async function textWords(pdfPage: PDFPageProxy): Promise<PageWord[]> {
  const words: PageWord[] = [];
  for (const run of await textRuns(pdfPage)) {
    for (const word of run.str.matchAll(/\S+/g)) {
      const [x = 0, y = 0, width = 0, height = 0] = run.box(word.index, word.index + word[0].length);
      words.push({ x, y, width, height, text: word[0] });
    }
  }
  return words;
}

/**
 * A page's runs of text, each able to say where a stretch of it is as fractions of the page. pdf.js gives each run
 * with its position and width; where a stretch starts and ends within the run is measured in the run's font family,
 * scaled to the run's real width, which is close enough for marking and selecting.
 */
async function textRuns(pdfPage: PDFPageProxy): Promise<{ str: string; box: (start: number, end: number) => MarkBox }[]> {
  const viewport: PageViewport = pdfPage.getViewport({ scale: 1 });
  const content = await pdfPage.getTextContent();
  const measure = document.createElement("canvas").getContext("2d");
  return content.items.flatMap((raw) => {
    if (!("str" in raw)) return [];
    const item = raw as TextItem;
    if (!item.str.trim()) return [];
    const [a = 0, b = 0, , , x = 0, y = 0] = item.transform;
    const size = item.height || Math.hypot(a, b);
    const family = content.styles[item.fontName]?.fontFamily ?? "sans-serif";
    const width = (text: string) => {
      if (!measure) return text.length;
      measure.font = `${size}px ${family}`;
      return measure.measureText(text).width;
    };
    const full = width(item.str) || item.str.length;
    const offset = (index: number) => x + (item.width * width(item.str.slice(0, index))) / full;
    return [
      {
        str: item.str,
        box: (start: number, end: number): MarkBox => {
          const [x1 = 0, y1 = 0] = viewport.convertToViewportPoint(offset(start), y - size * 0.2) as number[];
          const [x2 = 0, y2 = 0] = viewport.convertToViewportPoint(offset(end), y + size * 0.85) as number[];
          return [
            Math.min(x1, x2) / viewport.width,
            Math.min(y1, y2) / viewport.height,
            Math.abs(x2 - x1) / viewport.width,
            Math.abs(y2 - y1) / viewport.height,
          ];
        },
      },
    ];
  });
}
