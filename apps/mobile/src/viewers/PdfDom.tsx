"use dom";

import type { DOMProps } from "expo/dom";
import { getDocument, type PDFDocumentProxy } from "pdfjs-dist";
import * as pdfWorker from "pdfjs-dist/build/pdf.worker.mjs";
import { useEffect, useRef, useState } from "react";

// pdf.js parses on this thread instead of a worker: a web view's bundle has no separate worker file to start.
(globalThis as { pdfjsWorker?: unknown }).pdfjsWorker = pdfWorker;

/**
 * A PDF with pdf.js in a web view (docs/design.md §15.3), the same library as on the web: every page under the other,
 * each drawn when it comes near the screen, fitted to the width. Pinch zooms the page as in a browser.
 */
export default function PdfDom({
  data,
  background,
  pageLabel,
  onFailed,
}: {
  /** The file as base64. */
  data: string;
  background: string;
  /** "Page {{page}} of {{total}}". */
  pageLabel: string;
  onFailed: () => Promise<void>;
  dom?: DOMProps;
}) {
  const [pdf, setPdf] = useState<PDFDocumentProxy | null>(null);
  const [current, setCurrent] = useState(1);
  const failed = useRef(onFailed);
  useEffect(() => {
    failed.current = onFailed;
  }, [onFailed]);

  // The web view's page turns pinch-zoom off; small print in a PDF needs it.
  useEffect(() => {
    document
      .querySelector('meta[name="viewport"]')
      ?.setAttribute("content", "width=device-width, initial-scale=1, maximum-scale=5");
  }, []);

  useEffect(() => {
    const bytes = Uint8Array.from(atob(data), (c) => c.charCodeAt(0));
    const task = getDocument({ data: bytes });
    task.promise.then(setPdf, () => void failed.current());
    return () => void task.destroy();
  }, [data]);

  if (!pdf) return <div style={{ background, minHeight: "100vh" }} />;
  return (
    <div style={{ background, minHeight: "100vh", padding: "8px 0 48px" }}>
      {Array.from({ length: pdf.numPages }, (_, i) => (
        <Page key={i} pdf={pdf} number={i + 1} onVisible={setCurrent} />
      ))}
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
        {pageLabel.replace("{{page}}", String(current)).replace("{{total}}", String(pdf.numPages))}
      </div>
    </div>
  );
}

function Page({
  pdf,
  number,
  onVisible,
}: {
  pdf: PDFDocumentProxy;
  number: number;
  onVisible: (page: number) => void;
}) {
  const box = useRef<HTMLDivElement>(null);
  const canvas = useRef<HTMLCanvasElement>(null);
  const [ratio, setRatio] = useState(1.414);
  const [near, setNear] = useState(number <= 2);

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
    if (!element) return;
    const observer = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          // Pages far off screen give their canvas back, so a long PDF doesn't run out of memory.
          setNear(entry.isIntersecting);
          if (entry.intersectionRatio > 0.5) onVisible(number);
        }
      },
      { rootMargin: "100% 0px", threshold: [0, 0.5] },
    );
    observer.observe(element);
    return () => observer.disconnect();
  }, [number, onVisible]);

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
      // Twice the screen's density, so a pinch to read small print stays sharp.
      const scale =
        (box.current!.clientWidth / page.getViewport({ scale: 1 }).width) *
        window.devicePixelRatio *
        2;
      const viewport = page.getViewport({ scale });
      element.width = Math.floor(viewport.width);
      element.height = Math.floor(viewport.height);
      const task = page.render({ canvas: element, viewport });
      render = task;
      task.promise.catch(() => {});
    });
    return () => {
      cancelled = true;
      render?.cancel();
    };
  }, [near, pdf, number]);

  return (
    <div
      ref={box}
      style={{
        margin: "0 8px 8px",
        aspectRatio: `1 / ${ratio}`,
        background: "white",
        boxShadow: "0 1px 3px rgba(0,0,0,.3)",
      }}
    >
      <canvas ref={canvas} style={{ width: "100%", height: "100%", display: "block" }} />
    </div>
  );
}
