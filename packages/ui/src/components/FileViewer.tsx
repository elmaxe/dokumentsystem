import { formatBytes } from "@dokument/core";
import { lazy, Suspense, useEffect, useRef, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { cn } from "../lib/cn";
import type { FileItem } from "../lib/files";
import { Button, IconButton } from "./Button";
import { EmptyState } from "./EmptyState";
import { FileTypeIcon } from "./FileTypeIcon";
import { Icon } from "./Icon";
import { ActionMenu, type MenuAction } from "./Menu";
import { Progress } from "./Progress";

const PdfPreview = lazy(() => import("./PdfPreview"));

/** What the viewer body shows (docs/design.md §14). `src` is the server's preview URL, which is not audited. */
export type PreviewSource =
  | { type: "image" | "pdf" | "text" | "video" | "audio"; src: string }
  /** The worker is still making the rendition the viewer needs, e.g. for a HEIC photo. */
  | { type: "preparing" }
  | { type: "none" };

export type FileViewerProps = {
  item: FileItem;
  preview: PreviewSource;
  onClose: () => void;
  onDownload: () => void;
  onShare?: () => void;
  /** The "More" menu: rename, move, trash and so on. */
  menuActions?: MenuAction[];
  onAction?: (id: string) => void;
  /** The file's siblings, a swipe or arrow key away. Leave out at either end. */
  onPrevious?: () => void;
  onNext?: () => void;
  detailsOpen?: boolean;
  onDetailsChange?: (open: boolean) => void;
  /** The details panel, shown beside the preview when open. */
  details?: ReactNode;
  /** A note under the top bar, such as a newer version having arrived (§15.4). */
  banner?: ReactNode;
};

/** The file viewer (§14, §16.4): a full-screen preview with the file's actions, its siblings and its details. */
export function FileViewer({
  item,
  preview,
  onClose,
  onDownload,
  onShare,
  menuActions,
  onAction,
  onPrevious,
  onNext,
  detailsOpen,
  onDetailsChange,
  details,
  banner,
}: FileViewerProps) {
  const { t } = useTranslation();
  const swipe = useRef<{ x: number; y: number } | null>(null);

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.target instanceof HTMLInputElement || event.target instanceof HTMLTextAreaElement) return;
      if (document.querySelector("[role=menu],[role=dialog]")) return;
      if (event.key === "ArrowRight") onNext?.();
      else if (event.key === "ArrowLeft") onPrevious?.();
      else if (event.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose, onNext, onPrevious]);

  const noPreview = (failed: boolean) => (
    <EmptyState
      icon={failed ? "broken_image" : "visibility_off"}
      title={failed ? t("viewer.failedTitle") : t("viewer.noPreviewTitle")}
      text={failed ? t("viewer.failedText") : t("viewer.noPreviewText")}
      action={
        <Button icon="download" onClick={onDownload}>
          {t("actions.download")}
        </Button>
      }
      className="m-4 max-w-md bg-surface-container-low"
    />
  );

  return (
    <div className="flex h-dvh flex-col bg-surface-container-highest text-on-surface">
      <header className="flex h-16 shrink-0 items-center gap-2 bg-surface-container-low px-2 sm:px-4">
        <IconButton icon="arrow_back" label={t("viewer.close")} onClick={onClose} />
        <FileTypeIcon kind={item.kind} className="max-sm:hidden" />
        <h1 className="m-0 min-w-0 flex-1 truncate text-title-md" title={item.name}>
          {item.name}
        </h1>
        {onShare && (
          <>
            <Button variant="tonal" icon="person_add" onClick={onShare} className="max-md:hidden">
              {t("actions.share")}
            </Button>
            <IconButton icon="person_add" label={t("actions.share")} onClick={onShare} className="md:hidden" />
          </>
        )}
        <IconButton icon="download" label={t("actions.download")} onClick={onDownload} />
        {onDetailsChange && (
          <IconButton
            icon="info"
            label={t("files.showDetails")}
            selected={detailsOpen}
            onClick={() => onDetailsChange(!detailsOpen)}
          />
        )}
        {menuActions && onAction && menuActions.length > 0 && (
          <ActionMenu actions={menuActions} onAction={onAction} trigger={<IconButton icon="more_vert" label={t("common.more")} />} />
        )}
      </header>
      {banner && <div className="shrink-0 px-2 pt-2 sm:px-4">{banner}</div>}
      <div className="flex min-h-0 flex-1">
        <div
          className="relative flex min-w-0 flex-1 flex-col"
          onTouchStart={(event) => {
            const touch = event.touches[0];
            swipe.current = event.touches.length === 1 && touch ? { x: touch.clientX, y: touch.clientY } : null;
          }}
          onTouchEnd={(event) => {
            const start = swipe.current;
            const touch = event.changedTouches[0];
            swipe.current = null;
            if (!start || !touch) return;
            const dx = touch.clientX - start.x;
            if (Math.abs(dx) > 80 && Math.abs(dx) > 2 * Math.abs(touch.clientY - start.y)) (dx < 0 ? onNext : onPrevious)?.();
          }}
        >
          {preview.type === "image" ? (
            <ImagePreview key={preview.src} src={preview.src} alt={item.name} fallback={noPreview(true)} />
          ) : preview.type === "pdf" ? (
            <Suspense fallback={<Loading />}>
              <PdfPreview key={preview.src} src={preview.src} fallback={noPreview(true)} />
            </Suspense>
          ) : preview.type === "text" ? (
            <TextPreview key={preview.src} src={preview.src} fallback={noPreview(true)} />
          ) : preview.type === "video" ? (
            <MediaPreview key={preview.src} type="video" src={preview.src} fallback={noPreview(true)} />
          ) : preview.type === "audio" ? (
            <MediaPreview key={preview.src} type="audio" src={preview.src} item={item} fallback={noPreview(true)} />
          ) : preview.type === "preparing" ? (
            <Centered>
              <EmptyState
                icon="hourglass_top"
                title={t("viewer.preparingTitle")}
                text={t("viewer.preparingText")}
                className="m-4 max-w-md bg-surface-container-low"
              />
            </Centered>
          ) : (
            <Centered>{noPreview(false)}</Centered>
          )}
          {onPrevious && (
            <IconButton
              icon="chevron_left"
              label={t("viewer.previous")}
              onClick={onPrevious}
              className="absolute top-1/2 left-2 -translate-y-1/2 bg-surface-container shadow-elevation-1 max-sm:hidden"
            />
          )}
          {onNext && (
            <IconButton
              icon="chevron_right"
              label={t("viewer.next")}
              onClick={onNext}
              className="absolute top-1/2 right-2 -translate-y-1/2 bg-surface-container shadow-elevation-1 max-sm:hidden"
            />
          )}
        </div>
        {detailsOpen && details}
      </div>
    </div>
  );
}

function Centered({ children }: { children: ReactNode }) {
  return <div className="flex min-h-0 flex-1 items-center justify-center overflow-auto">{children}</div>;
}

export function Loading() {
  const { t } = useTranslation();
  return (
    <Centered>
      <Progress label={t("viewer.loading")} className="w-40" />
    </Centered>
  );
}

/** The floating pill under a preview with its page and zoom controls (§16.4). */
export function ViewerToolbar({ children }: { children: ReactNode }) {
  return (
    <div className="pointer-events-auto mx-auto mb-4 flex shrink-0 items-center gap-1 rounded-xl bg-surface-container px-2 py-1 shadow-elevation-1">
      {children}
    </div>
  );
}

export const ToolbarDivider = () => <span className="mx-1 h-6 w-px bg-outline-variant" />;

const zoomSteps = [0.5, 0.75, 1, 1.5, 2, 3, 4];

export function ZoomControls({ zoom, onZoom, disabled }: { zoom: number; onZoom: (zoom: number) => void; disabled?: boolean }) {
  const { t } = useTranslation();
  const index = zoomSteps.indexOf(zoom);
  return (
    <>
      <IconButton icon="zoom_out" label={t("viewer.zoomOut")} size="sm" disabled={disabled || index <= 0} onClick={() => onZoom(zoomSteps[index - 1] ?? zoom)} />
      <button
        type="button"
        title={t("viewer.fitToWindow")}
        disabled={disabled}
        onClick={() => onZoom(1)}
        className="tabular min-w-14 rounded-sm px-1 text-label-lg hover:bg-on-surface/8"
      >
        {t("viewer.zoomLevel", { percent: Math.round(zoom * 100) })}
      </button>
      <IconButton
        icon="zoom_in"
        label={t("viewer.zoomIn")}
        size="sm"
        disabled={disabled || index >= zoomSteps.length - 1}
        onClick={() => onZoom(zoomSteps[index + 1] ?? zoom)}
      />
    </>
  );
}

/** Lets the user drag a zoomed preview around, as well as scroll it. */
function useDragToPan() {
  const ref = useRef<HTMLDivElement>(null);
  const drag = useRef<{ x: number; y: number; left: number; top: number } | null>(null);
  return {
    ref,
    onPointerDown: (event: React.PointerEvent) => {
      const element = ref.current;
      if (!element || event.pointerType === "touch" || event.button !== 0) return;
      drag.current = { x: event.clientX, y: event.clientY, left: element.scrollLeft, top: element.scrollTop };
      element.setPointerCapture(event.pointerId);
    },
    onPointerMove: (event: React.PointerEvent) => {
      const element = ref.current;
      const start = drag.current;
      if (!element || !start) return;
      element.scrollLeft = start.left - (event.clientX - start.x);
      element.scrollTop = start.top - (event.clientY - start.y);
    },
    onPointerUp: () => {
      drag.current = null;
    },
  };
}

function ImagePreview({ src, alt, fallback }: { src: string; alt: string; fallback: ReactNode }) {
  const [zoom, setZoom] = useState(1);
  const [state, setState] = useState<"loading" | "loaded" | "failed">("loading");
  const [fit, setFit] = useState<{ width: number; height: number } | null>(null);
  const pan = useDragToPan();
  const image = useRef<HTMLImageElement>(null);

  // At 100 % the image fits the window with a margin; zooming scales that fitted size, and the area scrolls.
  useEffect(() => {
    const box = pan.ref.current;
    if (!box || state !== "loaded") return;
    const measure = () => {
      const element = image.current;
      if (!element?.naturalWidth) return;
      const scale = Math.min(1, (box.clientWidth - 32) / element.naturalWidth, (box.clientHeight - 32) / element.naturalHeight);
      setFit({ width: element.naturalWidth * scale, height: element.naturalHeight * scale });
    };
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(box);
    return () => observer.disconnect();
  }, [state, pan.ref]);

  if (state === "failed") return <Centered>{fallback}</Centered>;
  return (
    <>
      <div
        {...pan}
        className={cn("flex min-h-0 flex-1 overflow-auto", zoom > 1 && "cursor-grab active:cursor-grabbing")}
        onDoubleClick={() => setZoom(zoom === 1 ? 2 : 1)}
      >
        {state === "loading" && <Loading />}
        <img
          ref={image}
          src={src}
          alt={alt}
          draggable={false}
          onLoad={() => setState("loaded")}
          onError={() => setState("failed")}
          style={fit ? { width: fit.width * zoom, height: fit.height * zoom } : undefined}
          className={cn("m-auto max-w-none shrink-0 rounded-sm shadow-elevation-2 select-none", !fit && "invisible absolute")}
        />
      </div>
      <ViewerToolbar>
        <ZoomControls zoom={zoom} onZoom={setZoom} disabled={state !== "loaded"} />
      </ViewerToolbar>
    </>
  );
}

/** Text files are shown up to this many bytes; the rest is a download away. */
const textLimit = 1024 * 1024;

function TextPreview({ src, fallback }: { src: string; fallback: ReactNode }) {
  const { t, i18n } = useTranslation();
  const [text, setText] = useState<{ body: string; truncated: boolean } | "failed" | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    fetch(src, { headers: { Range: `bytes=0-${textLimit - 1}` }, signal: controller.signal, credentials: "same-origin" })
      .then(async (response) => {
        if (!response.ok) throw new Error(String(response.status));
        const total = Number(response.headers.get("Content-Range")?.split("/")[1] ?? 0);
        const bytes = await response.arrayBuffer();
        // A cut can land inside a multi-byte character; the decoder replaces that one character.
        setText({ body: new TextDecoder("utf-8").decode(bytes), truncated: total > bytes.byteLength });
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setText(error instanceof Error && error.name === "AbortError" ? null : "failed");
      });
    return () => controller.abort();
  }, [src]);

  if (text === "failed") return <Centered>{fallback}</Centered>;
  if (!text) return <Loading />;
  return (
    <div className="min-h-0 flex-1 overflow-auto p-4 sm:p-8">
      <div className="mx-auto max-w-5xl rounded-md bg-surface-container-lowest shadow-elevation-1">
        {text.truncated && (
          <p className="m-0 flex items-center gap-2 border-b border-outline-variant px-4 py-2 text-body-sm text-on-surface-variant">
            <Icon name="info" size="sm" />
            {t("viewer.truncated", { size: formatBytes(textLimit, i18n.language) })}
          </p>
        )}
        <pre className="m-0 overflow-x-auto p-4 font-mono text-body-sm break-words whitespace-pre-wrap">{text.body}</pre>
      </div>
    </div>
  );
}

function MediaPreview({ type, src, item, fallback }: { type: "video" | "audio"; src: string; item?: FileItem; fallback: ReactNode }) {
  const [failed, setFailed] = useState(false);
  if (failed) return <Centered>{fallback}</Centered>;
  return (
    <Centered>
      {type === "video" ? (
        <video
          src={src}
          controls
          autoPlay
          preload="metadata"
          onError={() => setFailed(true)}
          className="max-h-full max-w-full bg-black shadow-elevation-2 sm:rounded-sm"
        />
      ) : (
        <div className="m-4 flex w-full max-w-md flex-col items-center gap-6 rounded-lg bg-surface-container-low p-8 shadow-elevation-1">
          <span className="flex size-32 items-center justify-center rounded-lg bg-surface-container-high text-file-audio">
            <Icon name="music_note" filled size={64} />
          </span>
          {item && <p className="m-0 max-w-full truncate text-title-md">{item.name}</p>}
          <audio src={src} controls autoPlay preload="metadata" onError={() => setFailed(true)} className="w-full" />
        </div>
      )}
    </Centered>
  );
}
