"use dom";

import { findHits } from "@dokument/core";
import type { DOMProps } from "expo/dom";
import { getDocument, type PDFDocumentProxy, type PDFPageProxy } from "pdfjs-dist";
import * as pdfWorker from "pdfjs-dist/build/pdf.worker.mjs";
import { useEffect, useLayoutEffect, useRef, useState } from "react";

// pdf.js parses on this thread instead of a worker: a web view's bundle has no separate worker file to start.
(globalThis as { pdfjsWorker?: unknown }).pdfjsWorker = pdfWorker;

/** A box on a page, as fractions of its width and height: x, y, width, height. */
type MarkBox = number[];

/** Search hits to mark (docs/design.md §13.3): recognized boxes for scanned pages, words for the text layer. */
export type PdfMarks = {
  terms: string[];
  pages: { pageNo: number; boxes: MarkBox[] }[];
  /** The fill and edge of a mark: warning-container and warning. */
  fill: string;
  edge: string;
};

/**
 * A PDF with pdf.js in a web view (docs/design.md §15.3), the same library as on the web: every page under the other,
 * each drawn when it comes near the screen, fitted to the width. Pinch and double tap zoom; Android's web view does
 * neither for a page unless switched on natively, so the zoom is done here and pages are drawn again at the new size.
 */
export default function PdfDom({
  data,
  background,
  pageLabel,
  onFailed,
  marks,
  target,
  onPage,
}: {
  /** The file as base64. */
  data: string;
  background: string;
  /** "Page {{page}} of {{total}}". */
  pageLabel: string;
  onFailed: () => Promise<void>;
  marks?: PdfMarks | null;
  /** A page to scroll to; a new `key` scrolls again. */
  target?: { page: number; key: number } | null;
  /** The page mostly on screen, as it changes. */
  onPage?: (page: number) => Promise<void>;
  dom?: DOMProps;
}) {
  const [pdf, setPdf] = useState<PDFDocumentProxy | null>(null);
  const [current, setCurrent] = useState(1);
  const [scroller, setScroller] = useState<HTMLDivElement | null>(null);
  const content = useRef<HTMLDivElement>(null);
  const [zoom, setZoom] = useState(1);
  const zoomRef = useRef(1);
  // Where to scroll once the pages have their new size, so the point under the fingers stays there.
  const pendingScroll = useRef<{ left: number; top: number } | null>(null);
  const failed = useRef(onFailed);
  useEffect(() => {
    failed.current = onFailed;
  }, [onFailed]);
  const pageChanged = useRef(onPage);
  useEffect(() => {
    pageChanged.current = onPage;
  }, [onPage]);
  useEffect(() => {
    void pageChanged.current?.(current);
  }, [current]);

  // Scrolls to the page asked for: once the pages have their real sizes, so the place holds.
  const targetKey = target ? `${target.page}/${target.key}` : "";
  useEffect(() => {
    if (!pdf || !scroller || !targetKey) return;
    const page = Number(targetKey.split("/")[0]);
    const timer = setTimeout(() => {
      scroller.querySelector(`[data-page="${page}"]`)?.scrollIntoView({ block: "start" });
    }, 250);
    return () => clearTimeout(timer);
  }, [pdf, scroller, targetKey]);

  useEffect(() => {
    const bytes = Uint8Array.from(atob(data), (c) => c.charCodeAt(0));
    const task = getDocument({ data: bytes });
    task.promise.then(setPdf, () => void failed.current());
    return () => void task.destroy();
  }, [data]);

  useLayoutEffect(() => {
    zoomRef.current = zoom;
    const target = pendingScroll.current;
    if (scroller && target) scroller.scrollTo(target);
    pendingScroll.current = null;
  }, [zoom, scroller]);

  useEffect(() => {
    const element = content.current;
    if (!scroller || !element) return;
    let pinch: {
      distance: number;
      x: number;
      y: number;
      scrollLeft: number;
      scrollTop: number;
    } | null = null;
    let live = 1;
    // A tap is short and stays put. Without the time limit, two small nudges against the page's edge, where it can't
    // scroll any further, read as a double tap and zoomed back out.
    let tap: { x: number; y: number; moved: boolean; time: number } | null = null;
    let lastTap: { x: number; y: number; time: number } | null = null;

    const touch = (touches: TouchList, index: number) => touches.item(index)!;
    const spread = (touches: TouchList) =>
      Math.hypot(
        touch(touches, 0).clientX - touch(touches, 1).clientX,
        touch(touches, 0).clientY - touch(touches, 1).clientY,
      );

    // Zooms to `next` around a point on the screen, keeping that point of the page under it.
    const zoomTo = (next: number, x: number, y: number) => {
      const ratio = next / zoomRef.current;
      pendingScroll.current = {
        left: (scroller.scrollLeft + x) * ratio - x,
        top: (scroller.scrollTop + y) * ratio - y,
      };
      setZoom(next);
    };

    const start = (event: TouchEvent) => {
      if (event.touches.length === 1) {
        const first = touch(event.touches, 0);
        tap = { x: first.clientX, y: first.clientY, moved: false, time: Date.now() };
        return;
      }
      tap = null;
      lastTap = null;
      if (event.touches.length !== 2) return;
      const box = scroller.getBoundingClientRect();
      const [a, b] = [touch(event.touches, 0), touch(event.touches, 1)];
      pinch = {
        distance: spread(event.touches),
        x: (a.clientX + b.clientX) / 2 - box.left,
        y: (a.clientY + b.clientY) / 2 - box.top,
        scrollLeft: scroller.scrollLeft,
        scrollTop: scroller.scrollTop,
      };
      live = 1;
      element.style.transformOrigin = `${pinch.scrollLeft + pinch.x}px ${pinch.scrollTop + pinch.y}px`;
    };

    const move = (event: TouchEvent) => {
      if (tap) {
        const first = touch(event.touches, 0);
        if (Math.hypot(first.clientX - tap.x, first.clientY - tap.y) > 10) tap.moved = true;
      }
      if (!pinch || event.touches.length !== 2) return;
      event.preventDefault();
      const next = clampZoom((zoomRef.current * spread(event.touches)) / pinch.distance);
      live = next / zoomRef.current;
      // Stretched as it is until the fingers lift, then drawn again at the new size.
      element.style.transform = `scale(${live})`;
    };

    const end = (event: TouchEvent) => {
      if (pinch && event.touches.length < 2) {
        const { x, y, scrollLeft, scrollTop } = pinch;
        pinch = null;
        element.style.transform = "";
        pendingScroll.current = {
          left: (scrollLeft + x) * live - x,
          top: (scrollTop + y) * live - y,
        };
        setZoom(zoomRef.current * live);
        return;
      }
      if (!tap || event.touches.length > 0) return;
      const now = Date.now();
      if (tap.moved || now - tap.time > 250) {
        tap = null;
        lastTap = null;
        return;
      }
      const box = scroller.getBoundingClientRect();
      const x = tap.x - box.left;
      const y = tap.y - box.top;
      if (lastTap && now - lastTap.time < 300 && Math.hypot(x - lastTap.x, y - lastTap.y) < 40) {
        lastTap = null;
        zoomTo(zoomRef.current > 1 ? 1 : 2.5, x, y);
      } else {
        lastTap = { x, y, time: now };
      }
      tap = null;
    };

    scroller.addEventListener("touchstart", start, { passive: true });
    scroller.addEventListener("touchmove", move, { passive: false });
    scroller.addEventListener("touchend", end);
    scroller.addEventListener("touchcancel", end);
    return () => {
      scroller.removeEventListener("touchstart", start);
      scroller.removeEventListener("touchmove", move);
      scroller.removeEventListener("touchend", end);
      scroller.removeEventListener("touchcancel", end);
    };
  }, [scroller, pdf]);

  return (
    <div
      ref={setScroller}
      style={{
        position: "absolute",
        inset: 0,
        overflow: "auto",
        background,
        touchAction: "pan-x pan-y",
      }}
    >
      {pdf ? (
        <div
          ref={content}
          style={{ width: `${zoom * 100}%`, padding: "8px 0 48px", boxSizing: "border-box" }}
        >
          {Array.from({ length: pdf.numPages }, (_, i) => (
            <Page
              key={i}
              pdf={pdf}
              number={i + 1}
              zoom={zoom}
              root={scroller}
              onVisible={setCurrent}
              marks={marks}
            />
          ))}
        </div>
      ) : null}
      {pdf ? (
        <div
          style={{
            position: "fixed",
            bottom: 16,
            left: "50%",
            transform: "translateX(-50%)",
            padding: "4px 12px",
            borderRadius: 16,
            background: "rgba(0, 0, 0, 0.6)",
            color: "white",
            font: "14px system-ui, sans-serif",
          }}
        >
          {pageLabel
            .replace("{{page}}", String(current))
            .replace("{{total}}", String(pdf.numPages))}
        </div>
      ) : null}
    </div>
  );
}

const maxZoom = 5;
const clampZoom = (value: number) => Math.min(maxZoom, Math.max(1, value));

/** A page's canvas stays under this many pixels however far it's zoomed, so a phone's memory holds out. */
const maxPixels = 16_000_000;

function Page({
  pdf,
  number,
  zoom,
  root,
  onVisible,
  marks,
}: {
  pdf: PDFDocumentProxy;
  number: number;
  zoom: number;
  root: HTMLDivElement | null;
  onVisible: (page: number) => void;
  marks?: PdfMarks | null;
}) {
  const box = useRef<HTMLDivElement>(null);
  const canvas = useRef<HTMLCanvasElement>(null);
  const [ratio, setRatio] = useState(1.414);
  const [near, setNear] = useState(number <= 2);
  const [found, setFound] = useState<{ key: string; boxes: MarkBox[] }>({ key: "", boxes: [] });
  const hit = marks?.pages.find((page) => page.pageNo === number);
  const boxesKey = hit ? JSON.stringify(hit.boxes) : "";
  const termsKey = hit ? (marks?.terms.join(" ") ?? "") : "";
  const wanted = near && (boxesKey || termsKey) ? `${boxesKey}|${termsKey}` : "";
  const shown = wanted && found.key === wanted ? found.boxes : [];

  // Hits on this page: the recognized boxes for a scan, otherwise the words found in the page's own text.
  useEffect(() => {
    if (!wanted) return;
    let cancelled = false;
    const boxes = boxesKey ? (JSON.parse(boxesKey) as MarkBox[]) : [];
    void (async () => {
      const page = await pdf.getPage(number);
      const next = boxes.length > 0 ? boxes : await textMarks(page, termsKey.split(" "));
      if (!cancelled) setFound({ key: wanted, boxes: next });
    })();
    return () => {
      cancelled = true;
    };
  }, [wanted, pdf, number, boxesKey, termsKey]);

  useEffect(() => {
    let cancelled = false;
    void pdf.getPage(number).then((page) => {
      const viewport = page.getViewport({ scale: 1 });
      if (!cancelled) setRatio(viewport.height / viewport.width);
    });
    return () => {
      cancelled = true;
    };
  }, [pdf, number]);

  useEffect(() => {
    const element = box.current;
    if (!element || !root) return;
    const observer = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          // Pages far off screen give their canvas back, so a long PDF doesn't run out of memory.
          setNear(entry.isIntersecting);
        }
      },
      { root, rootMargin: "100% 0px" },
    );
    // The page the person is on is the one across the middle of the screen.
    const middle = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) if (entry.isIntersecting) onVisible(number);
      },
      { root, rootMargin: "-50% 0px -50% 0px" },
    );
    observer.observe(element);
    middle.observe(element);
    return () => {
      observer.disconnect();
      middle.disconnect();
    };
  }, [number, onVisible, root]);

  useEffect(() => {
    const element = canvas.current;
    if (!element || !box.current) return;
    if (!near) {
      element.width = 0;
      element.height = 0;
      return;
    }
    let render: { cancel: () => void } | null = null;
    let cancelled = false;
    void pdf.getPage(number).then((page) => {
      if (cancelled) return;
      const width = page.getViewport({ scale: 1 }).width;
      const sharp = (box.current!.clientWidth / width) * window.devicePixelRatio;
      const natural = page.getViewport({ scale: sharp });
      const scale = sharp * Math.min(1, Math.sqrt(maxPixels / (natural.width * natural.height)));
      const viewport = page.getViewport({ scale });
      // Drawn off screen and copied over when done, so the page doesn't go blank while it's drawn at a new zoom.
      const drawing = document.createElement("canvas");
      drawing.width = Math.floor(viewport.width);
      drawing.height = Math.floor(viewport.height);
      const task = page.render({ canvas: drawing, viewport });
      render = task;
      task.promise.then(
        () => {
          if (cancelled) return;
          element.width = drawing.width;
          element.height = drawing.height;
          element.getContext("2d")?.drawImage(drawing, 0, 0);
          drawing.width = 0;
        },
        () => {},
      );
    });
    return () => {
      cancelled = true;
      render?.cancel();
    };
  }, [near, pdf, number, zoom]);

  return (
    <div
      ref={box}
      data-page={number}
      style={{
        position: "relative",
        margin: "0 8px 8px",
        aspectRatio: `1 / ${ratio}`,
        background: "white",
        boxShadow: "0 1px 3px rgba(0,0,0,.3)",
      }}
    >
      <canvas ref={canvas} style={{ width: "100%", height: "100%", display: "block" }} />
      {marks
        ? shown.map(([x = 0, y = 0, width = 0, height = 0], i) => (
            <div
              key={i}
              style={{
                position: "absolute",
                left: `${x * 100}%`,
                top: `${y * 100}%`,
                width: `${width * 100}%`,
                height: `${height * 100}%`,
                background: marks.fill,
                outline: `2px solid ${marks.edge}`,
                borderRadius: 2,
                mixBlendMode: "multiply",
                pointerEvents: "none",
              }}
            />
          ))
        : null}
    </div>
  );
}

type TextItem = { str: string; transform: number[]; width: number; height: number; fontName: string };

/**
 * Where the search hits are in a page's text, as fractions of the page, as the web's viewer finds them: pdf.js gives
 * each run of text with its position and width, and where a hit starts and ends within it is measured in the run's
 * font family, scaled to the run's real width, which is close enough for marking.
 */
async function textMarks(page: PDFPageProxy, terms: string[]): Promise<MarkBox[]> {
  const viewport = page.getViewport({ scale: 1 });
  const content = await page.getTextContent();
  const measure = document.createElement("canvas").getContext("2d");
  const marks: MarkBox[] = [];
  for (const raw of content.items) {
    if (!("str" in raw)) continue;
    const item = raw as TextItem;
    if (!item.str.trim()) continue;
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
    for (const [start, end] of findHits(item.str, terms)) {
      const [x1 = 0, y1 = 0] = viewport.convertToViewportPoint(offset(start), y - size * 0.2) as number[];
      const [x2 = 0, y2 = 0] = viewport.convertToViewportPoint(offset(end), y + size * 0.85) as number[];
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
