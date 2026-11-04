import type { ScriptorView } from "@dokument/scriptor";
import { useEffect, useRef, useState, type ReactNode } from "react";
import { cn } from "../lib/cn";
import { Loading, ViewerToolbar, ZoomControls } from "./FileViewer";

/** Why Scriptor didn't show a file: it couldn't read it, or this build doesn't include it (no Rust when it was built). */
export type WordFailure = "unreadable" | "missing";

class EngineMissing extends Error {
  constructor(cause?: unknown) {
    super("Scriptor isn't built into this web app: it needs Rust and wasm-pack (docs/design.md §14).", { cause });
  }
}

/** Scriptor draws at 96 dpi at zoom 1: a twip is 1/1440 inch. */
const pxPerTwip = 96 / 1440;

/**
 * Word files in the viewer, drawn by Scriptor (docs/design.md §14, decision 42): a Rust engine compiled to WebAssembly
 * that lays out the .docx itself, with the fonts it needs built in, so nothing leaves the browser and no PDF has to be
 * made first. Read-only; text can be selected and copied. The engine (16 MB, 6 MB compressed) loads only when a Word
 * file is opened.
 *
 * At 100 % the page fits the window's width, as in the PDF viewer. `onFailed` is called when Scriptor can't read the
 * file, or isn't in this build at all ("missing"), so the viewer can show the PDF instead.
 */
export default function WordPreview({
  src,
  onFailed,
  onSave,
  fallback,
}: {
  src: string;
  onFailed: (reason: WordFailure) => void;
  /** Ctrl+S, which Word users press out of habit: the viewer downloads the original. */
  onSave: () => void;
  fallback: ReactNode;
}) {
  const box = useRef<HTMLDivElement>(null);
  const mount = useRef<HTMLDivElement>(null);
  const [view, setView] = useState<ScriptorView | null>(null);
  const [state, setState] = useState<"loading" | "ready" | "failed">("loading");
  const [zoom, setZoom] = useState(1);
  const [width, setWidth] = useState(0);
  const latest = useRef({ onFailed, onSave });
  useEffect(() => {
    latest.current = { onFailed, onSave };
  });

  useEffect(() => {
    const host = mount.current;
    if (!host) return;
    let cancelled = false;
    let created: ScriptorView | null = null;
    (async () => {
      // A stub without Rust, or no build at all (a dev server started before packages/scriptor was built).
      const engine = import("@dokument/scriptor").then(
        (module) => (module.available ? module : Promise.reject(new EngineMissing())),
        () => Promise.reject(new EngineMissing()),
      );
      const [{ ScriptorView }, response] = await Promise.all([engine, fetch(src, { credentials: "include" })]);
      if (!response.ok) throw new Error(`The file couldn't be loaded (${response.status}).`);
      const bytes = new Uint8Array(await response.arrayBuffer());
      if (cancelled) return;
      // The engine loads here, so a failure is the build's (a missing or stale .wasm), not the file's.
      const shown = await ScriptorView.create(host, {
        mode: "read",
        selectable: true,
        onSave: () => latest.current.onSave(),
      }).catch((error: unknown) => Promise.reject(new EngineMissing(error)));
      created = shown;
      if (cancelled) return;
      shown.loadDocx(bytes);
      setView(shown);
      setState("ready");
    })().catch((error: unknown) => {
      if (cancelled) return;
      const missing = error instanceof EngineMissing;
      console.warn(missing ? "Scriptor isn't in this build of the web app." : "Scriptor couldn't show this file.", error);
      setState("failed");
      latest.current.onFailed(missing ? "missing" : "unreadable");
    });
    return () => {
      cancelled = true;
      created?.destroy();
      host.replaceChildren();
      setView(null);
    };
  }, [src]);

  useEffect(() => {
    const element = box.current;
    if (!element) return;
    const observer = new ResizeObserver(([entry]) => entry && setWidth(entry.contentRect.width));
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  // The same fit as PDFs: the window's width, but never wider than pdf.js draws the page at 150 % (it counts a point as
  // a pixel), so a Word file and its PDF are the same size.
  useEffect(() => {
    if (!view || width === 0) return;
    const twips = view.pageGeometry().width;
    if (twips > 0) view.setZoom((Math.min(width - 32, (twips / 20) * 1.5) / (twips * pxPerTwip)) * zoom);
  }, [view, width, zoom]);

  if (state === "failed") return <div className="flex min-h-0 flex-1 items-center justify-center">{fallback}</div>;
  return (
    <>
      <div ref={box} className="relative min-h-0 flex-1 overflow-auto py-4 text-center">
        {state === "loading" && (
          <div className="absolute inset-0 flex">
            <Loading />
          </div>
        )}
        <div ref={mount} data-testid="word-preview" className={cn("inline-block text-left", state === "loading" && "invisible")} />
      </div>
      <ViewerToolbar>
        <ZoomControls zoom={zoom} onZoom={setZoom} disabled={state !== "ready"} />
      </ViewerToolbar>
    </>
  );
}
