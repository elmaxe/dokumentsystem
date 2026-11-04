import { formatBytes, type PageWord } from "@dokument/core";
import { lazy, Suspense, useEffect, useRef, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { cn } from "../lib/cn";
import type { FileItem } from "../lib/files";
import { Banner } from "./Banner";
import { Button, IconButton } from "./Button";
import { EmptyState } from "./EmptyState";
import { FileTypeIcon } from "./FileTypeIcon";
import { Icon } from "./Icon";
import { ActionMenu, type MenuAction } from "./Menu";
import { Progress } from "./Progress";
import { leadInMs, type SeekPreviews } from "../lib/media";
import { MediaPlayer } from "./MediaPlayer";
import { TranscriptPanel, type TranscriptView } from "./Transcript";
import { TextControls, TextFloatingToggle, useWords, WordLayer, type ViewerText } from "./WordLayer";
import type { WordFailure } from "./WordPreview";

const PdfPreview = lazy(() => import("./PdfPreview"));
const CodePreview = lazy(() => import("./CodePreview"));
const MarkdownPreview = lazy(() => import("./MarkdownPreview"));
const WordPreview = lazy(() => import("./WordPreview"));

/** What the viewer body shows (docs/design.md §14). `src` is the server's preview URL, which is not audited. */
export type PreviewSource =
  | { type: "image" | "pdf" | "text" | "markdown"; src: string }
  /** A .docx drawn by Scriptor from the original bytes, with the PDF Gotenberg made of it as the other view (§14). */
  /** `preparing`: the PDF is still being made, so it may yet come if Scriptor can't show the file. */
  | { type: "word"; src: string; pdf?: string; preparing?: boolean }
  /** `fallbacks` are tried in turn if the browser can't play `src`, e.g. the playable copy after the HEVC original. */
  /**
   * `poster` is a video's poster frame or an audio file's cover art; `waveform` is where an audio file's waveform is,
   * drawn as the timeline (§14).
   */
  | {
      type: "video" | "audio";
      src: string;
      fallbacks?: string[];
      poster?: string;
      seekPreviews?: SeekPreviews;
      waveform?: string;
    }
  /** The worker is still making the rendition the viewer needs, e.g. for a HEIC photo. */
  | { type: "preparing" }
  /** A video or audio file browsers can't play, while the worker makes a copy that plays. `progress` is 0–100. */
  | { type: "converting"; waiting: boolean; progress?: number }
  /** A video or audio file browsers can't play and no copy can be made of: copies are off, or the file is damaged. */
  | { type: "unplayable" }
  | { type: "none" };

/** A hit's box on a page, as `[x, y, width, height]` fractions of the page. */
export type MarkBox = readonly number[];

/** Search hits to mark in the preview, when the file was opened from search results (docs/design.md §13.3). */
export type ViewerMarks = {
  query: string;
  terms: readonly string[];
  /** Pages with hits, from 1. Boxes are for recognized text; a text layer is searched by the viewer itself. */
  pages: readonly { pageNo: number; boxes: readonly MarkBox[] }[];
  /** The page to open on: the best hit. */
  startPage?: number;
  /** In a recording: where the hits are said, in milliseconds. */
  times?: readonly number[];
  onClear: () => void;
};

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
  marks?: ViewerMarks;
  /** "Result 3 of 23", when the siblings are search results. */
  position?: string;
  /** A note under the top bar, such as a newer version having arrived (§15.4). */
  banner?: ReactNode;
  /** What's said in a video or audio file, shown beside the player (§11.1). */
  transcript?: TranscriptView;
  /** Transcribes the recording again from the start, e.g. after the admin picked another model. */
  onTranscriptRedo?: () => void;
  /** WebVTT captions for a video, from its transcript. */
  captions?: string;
  /** Where a recording starts playing, in milliseconds: just before the hit it was opened from. */
  startAtMs?: number;
  /**
   * Words the server recognized in scans and photos, for the Text toggle (§14): the pages that have any, and a page's
   * words. Leave out for an older version, whose words aren't served.
   */
  recognized?: { pages: readonly number[]; wordsOn: (page: number) => Promise<readonly PageWord[]> };
  /** Whether the Text toggle is on. Kept by the page, so it stays on while stepping between files. */
  textShown?: boolean;
  onTextShownChange?: (shown: boolean) => void;
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
  marks,
  position,
  banner,
  transcript,
  onTranscriptRedo,
  captions,
  startAtMs,
  recognized,
  textShown = false,
  onTextShownChange,
}: FileViewerProps) {
  const { t } = useTranslation();
  const swipe = useRef<{ x: number; y: number } | null>(null);
  const markedPages = marks?.pages.map((p) => p.pageNo).sort((a, b) => a - b) ?? [];
  const [page, setPage] = useState(marks?.startPage ?? markedPages[0] ?? 1);
  const pageIndex = markedPages.indexOf(page);
  const previousMarked = markedPages.filter((p) => p < page).at(-1);
  const nextMarked = markedPages.find((p) => p > page);
  const boxesOn = (pageNo: number) => marks?.pages.find((p) => p.pageNo === pageNo)?.boxes ?? [];
  // Photos and scans with recognized words, and every PDF, which may have text of its own.
  const text: ViewerText | undefined =
    onTextShownChange && (preview.type === "pdf" || (preview.type === "image" && recognized?.pages.includes(1)))
      ? {
          shown: textShown,
          onShownChange: onTextShownChange,
          wordsOn: (pageNo) => (recognized?.pages.includes(pageNo) ? recognized.wordsOn(pageNo) : null),
        }
      : undefined;
  const [markdownView, setMarkdownView] = useState<"formatted" | "source">("formatted");
  // A Word file is shown by Scriptor unless the person picks the PDF, or Scriptor can't read it.
  const [wordView, setWordView] = useState<"word" | "pdf">("word");
  const [wordFailure, setWordFailure] = useState<{ src: string; reason: WordFailure } | null>(null);
  const wordFailed = wordFailure?.src ?? null;
  const word = preview.type === "word" ? preview : null;
  const wordAsPdf = word?.pdf && (wordView === "pdf" || wordFailed === word.src) ? word.pdf : null;
  const [wrap, setWrap] = useStoredFlag("dokument.viewer.wrap");
  const [findRequest, setFindRequest] = useState(0);
  const showsCode = preview.type === "text" || (preview.type === "markdown" && markdownView === "source");
  const playing = preview.type === "video" || preview.type === "audio";
  const media = useRef<HTMLMediaElement | null>(null);
  const [currentMs, setCurrentMs] = useState(startAtMs ?? 0);
  const [transcriptOpen, setTranscriptOpen] = useState(true);
  const showsTranscript = playing && !!transcript && transcriptOpen;
  const times = playing ? [...(marks?.times ?? [])].sort((a, b) => a - b) : [];
  // The hit being heard: the last one started, counting the lead-in before it.
  const timeIndex = times.findLastIndex((time) => time - leadInMs <= currentMs + 500);
  const seek = (ms: number) => {
    const element = media.current;
    if (!element) return;
    element.currentTime = Math.max(0, ms) / 1000;
    setCurrentMs(Math.max(0, ms));
    void element.play().catch(() => {});
  };
  const seekToHit = (index: number) => {
    const time = times[index];
    if (time !== undefined) seek(time - leadInMs);
  };

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

  const preparing = (
    <EmptyState
      icon="hourglass_top"
      title={t("viewer.preparingTitle")}
      text={t("viewer.preparingText")}
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
        {marks && (
          <div
            role="group"
            aria-label={t("viewer.matches", { query: marks.query })}
            className="flex h-10 shrink-0 items-center gap-1 rounded-xl bg-surface-container-high pr-1 pl-3 max-sm:gap-0 max-sm:pl-1"
          >
            <Icon name="search" size="sm" className="text-on-surface-variant max-sm:hidden" />
            <span className="max-w-40 truncate px-1 max-md:hidden">{marks.query}</span>
            {times.length > 0 && (
              <>
                <span className="tabular px-1 text-label-lg text-on-surface-variant max-sm:sr-only">
                  {t("viewer.matchPosition", { current: timeIndex < 0 ? "–" : timeIndex + 1, total: times.length })}
                </span>
                <IconButton
                  icon="keyboard_arrow_up"
                  label={t("viewer.previousSpokenMatch")}
                  size="sm"
                  disabled={timeIndex <= 0}
                  onClick={() => seekToHit(timeIndex - 1)}
                />
                <IconButton
                  icon="keyboard_arrow_down"
                  label={t("viewer.nextSpokenMatch")}
                  size="sm"
                  disabled={timeIndex >= times.length - 1}
                  onClick={() => seekToHit(timeIndex + 1)}
                />
              </>
            )}
            {preview.type === "pdf" && markedPages.length > 0 && (
              <>
                <span className="tabular px-1 text-label-lg text-on-surface-variant max-sm:sr-only" title={pageIndex < 0 ? t("viewer.noMatchesHere") : undefined}>
                  {t("viewer.matchPosition", { current: pageIndex < 0 ? "–" : pageIndex + 1, total: markedPages.length })}
                </span>
                <IconButton
                  icon="keyboard_arrow_up"
                  label={t("viewer.previousMatch")}
                  size="sm"
                  disabled={previousMarked === undefined}
                  onClick={() => previousMarked && setPage(previousMarked)}
                />
                <IconButton
                  icon="keyboard_arrow_down"
                  label={t("viewer.nextMatch")}
                  size="sm"
                  disabled={nextMarked === undefined}
                  onClick={() => nextMarked && setPage(nextMarked)}
                />
              </>
            )}
            <IconButton icon="close" label={t("viewer.stopMarking")} size="sm" onClick={marks.onClear} />
          </div>
        )}
        {preview.type === "markdown" && <MarkdownViewToggle value={markdownView} onChange={setMarkdownView} />}
        {word?.pdf && wordFailed !== word.src && <WordViewToggle value={wordView} onChange={setWordView} />}
        {showsCode && (
          <>
            <IconButton icon="wrap_text" label={t("code.wrap")} selected={wrap} onClick={() => setWrap(!wrap)} />
            <IconButton icon="search" label={t("code.findInFile")} onClick={() => setFindRequest((n) => n + 1)} className="max-sm:hidden" />
          </>
        )}
        {playing && transcript && (
          <IconButton
            icon="subtitles"
            label={transcriptOpen ? t("transcript.hide") : t("transcript.show")}
            selected={transcriptOpen}
            onClick={() => setTranscriptOpen(!transcriptOpen)}
          />
        )}
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
      {(banner || wordAsPdf) && (
        <div className="shrink-0 px-2 pt-2 sm:px-4">
          {banner ?? (
            <Banner icon="picture_as_pdf">{wordFailed !== word?.src
                ? t("viewer.shownAsPdf")
                : wordFailure?.reason === "missing"
                  ? t("viewer.wordMissingAsPdf")
                  : t("viewer.wordFailedAsPdf")}</Banner>
          )}
        </div>
      )}
      <div className={cn("flex min-h-0 flex-1", showsTranscript && "max-lg:flex-col")}>
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
            <ImagePreview key={preview.src} src={preview.src} alt={item.name} boxes={boxesOn(1)} text={text} fallback={noPreview(true)} />
          ) : preview.type === "pdf" || wordAsPdf ? (
            <Suspense fallback={<Loading />}>
              <PdfPreview
                key={wordAsPdf ?? (preview.type === "pdf" ? preview.src : "")}
                src={wordAsPdf ?? (preview.type === "pdf" ? preview.src : "")}
                page={page}
                onPageChange={setPage}
                terms={marks?.terms}
                boxes={boxesOn(page)}
                text={text}
                fallback={noPreview(true)}
              />
            </Suspense>
          ) : preview.type === "word" ? (
            <Suspense fallback={<Loading />}>
              <WordPreview
                key={preview.src}
                src={preview.src}
                onFailed={(reason) => setWordFailure({ src: preview.src, reason })}
                onSave={onDownload}
                fallback={preview.preparing ? preparing : noPreview(wordFailure?.reason !== "missing")}
              />
            </Suspense>
          ) : preview.type === "text" || preview.type === "markdown" ? (
            <TextPreview
              key={preview.src}
              src={preview.src}
              fileName={item.name}
              formatted={preview.type === "markdown" && markdownView === "formatted"}
              wrap={wrap}
              findRequest={findRequest}
              terms={marks?.terms}
              fallback={noPreview(true)}
            />
          ) : preview.type === "video" || preview.type === "audio" ? (
            <Centered>
              <MediaPlayer
                key={preview.src}
                type={preview.type}
                sources={[preview.src, ...(preview.fallbacks ?? [])]}
                poster={preview.type === "video" ? preview.poster : undefined}
                seekPreviews={preview.seekPreviews}
                cover={<AudioCover name={item.name} src={preview.poster} />}
                waveform={preview.waveform}
                fallback={noPreview(true)}
                mediaRef={media}
                startAtMs={startAtMs}
                onTime={setCurrentMs}
                onSeek={seek}
                ticks={times}
                durationMs={transcript?.durationMs ?? undefined}
                captionLines={preview.type === "video" && captions ? transcript?.lines : undefined}
                captions={captions}
                captionsLanguage={transcript?.language ?? undefined}
              />
            </Centered>
          ) : preview.type === "converting" ? (
            <Centered>
              <EmptyState
                icon={item.kind === "audio" ? "music_note" : "movie"}
                title={t("viewer.convertingTitle")}
                text={preview.waiting ? t("viewer.convertingWaiting") : t("viewer.convertingText")}
                action={
                  <div className="mt-2 flex w-full flex-col items-center gap-6">
                    <Progress
                      value={preview.waiting ? undefined : preview.progress}
                      label={t("viewer.convertingTitle")}
                      className="w-full max-w-64"
                    />
                    <Button variant="outlined" icon="download" onClick={onDownload}>
                      {t("actions.download")}
                    </Button>
                  </div>
                }
                className="m-4 max-w-md bg-surface-container-low"
              />
            </Centered>
          ) : preview.type === "unplayable" ? (
            <Centered>
              <EmptyState
                icon="videocam_off"
                title={t("viewer.unplayableTitle")}
                text={t("viewer.unplayableText")}
                action={
                  <Button icon="download" onClick={onDownload}>
                    {t("actions.download")}
                  </Button>
                }
                className="m-4 max-w-md bg-surface-container-low"
              />
            </Centered>
          ) : preview.type === "preparing" ? (
            <Centered>{preparing}</Centered>
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
          {position && (
            <span className="tabular pointer-events-none absolute bottom-6 left-4 text-body-sm text-on-surface-variant max-md:hidden">
              {position}
            </span>
          )}
        </div>
        {showsTranscript && (
          <TranscriptPanel
            transcript={transcript}
            currentMs={currentMs}
            terms={marks?.terms}
            onSeek={seek}
            onRedo={onTranscriptRedo}
            onClose={() => setTranscriptOpen(false)}
            className="lg:w-96 lg:shrink-0 lg:border-l max-lg:h-[45%] max-lg:border-t"
          />
        )}
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

/** Hits on a page, drawn over it like a highlighter. */
export function MarkOverlay({ boxes }: { boxes: readonly MarkBox[] }) {
  return (
    <span aria-hidden className="pointer-events-none absolute inset-0">
      {boxes.map(([x = 0, y = 0, w = 0, h = 0], i) => (
        <span
          key={i}
          data-mark
          className="absolute rounded-xs bg-warning-container outline-2 outline-warning mix-blend-multiply"
          style={{ left: `${x * 100}%`, top: `${y * 100}%`, width: `${w * 100}%`, height: `${h * 100}%` }}
        />
      ))}
    </span>
  );
}

function ImagePreview({
  src,
  alt,
  boxes,
  text,
  fallback,
}: {
  src: string;
  alt: string;
  boxes: readonly MarkBox[];
  text?: ViewerText;
  fallback: ReactNode;
}) {
  const [zoom, setZoom] = useState(1);
  const [state, setState] = useState<"loading" | "loaded" | "failed">("loading");
  const [fit, setFit] = useState<{ width: number; height: number } | null>(null);
  const pan = useDragToPan();
  const image = useRef<HTMLImageElement>(null);
  const words = useWords(text?.shown ? () => text.wordsOn(1) ?? Promise.resolve([]) : null, src);

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
        <span
          style={fit ? { width: fit.width * zoom, height: fit.height * zoom } : undefined}
          className={cn("relative m-auto shrink-0", !fit && "invisible absolute")}
        >
          <img
            ref={image}
            src={src}
            alt={alt}
            draggable={false}
            onLoad={() => setState("loaded")}
            onError={() => setState("failed")}
            className="size-full max-w-none rounded-sm shadow-elevation-2 select-none"
          />
          {fit && words && <WordLayer words={words} />}
          {fit && boxes.length > 0 && <MarkOverlay boxes={boxes} />}
        </span>
      </div>
      {text && state === "loaded" && <TextFloatingToggle text={text} />}
      <ViewerToolbar>
        <ZoomControls zoom={zoom} onZoom={setZoom} disabled={state !== "loaded"} />
        {text && <TextControls text={text} words={words} />}
      </ViewerToolbar>
    </>
  );
}

/** Text files are shown up to this many bytes; the rest is a download away. */
const textLimit = 1024 * 1024;

function TextPreview({
  src,
  fileName,
  formatted,
  wrap,
  findRequest,
  terms,
  fallback,
}: {
  src: string;
  fileName: string;
  formatted: boolean;
  wrap: boolean;
  findRequest: number;
  terms?: readonly string[];
  fallback: ReactNode;
}) {
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
  const truncated = text.truncated && (
    <p className="m-0 flex shrink-0 items-center gap-2 border-b border-outline-variant px-4 py-2 text-body-sm text-on-surface-variant">
      <Icon name="info" size="sm" />
      {t("viewer.truncated", { size: formatBytes(textLimit, i18n.language) })}
    </p>
  );
  if (formatted) {
    return (
      <div className="min-h-0 flex-1 overflow-auto p-2 sm:p-4">
        <div className="mx-auto max-w-3xl overflow-hidden rounded-md bg-surface-container-lowest shadow-elevation-1">
          {truncated}
          <div className="px-5 py-6 sm:px-12 sm:py-10">
            <Suspense fallback={<Loading />}>
              <MarkdownPreview text={text.body} terms={terms} />
            </Suspense>
          </div>
        </div>
      </div>
    );
  }
  return (
    <div className="flex min-h-0 flex-1 flex-col p-2 sm:p-4">
      <div className="mx-auto flex min-h-0 w-full max-w-6xl flex-1 flex-col overflow-hidden rounded-md bg-surface-container-lowest shadow-elevation-1">
        {truncated}
        <div className="min-h-0 flex-1">
          <Suspense fallback={<Loading />}>
            <CodePreview text={text.body} fileName={fileName} wrap={wrap} terms={terms} findRequest={findRequest} />
          </Suspense>
        </div>
      </div>
    </div>
  );
}

/** Formatted or source, for Markdown files (§14). */
function MarkdownViewToggle({ value, onChange }: { value: "formatted" | "source"; onChange: (value: "formatted" | "source") => void }) {
  const { t } = useTranslation();
  const options = [
    { id: "formatted", icon: "article", label: t("markdown.formatted") },
    { id: "source", icon: "code", label: t("markdown.source") },
  ] as const;
  return <ViewToggle label={t("markdown.showAs")} options={options} value={value} onChange={onChange} />;
}

/** Word files: as Scriptor draws them, or as the PDF Gotenberg made (§14). */
function WordViewToggle({ value, onChange }: { value: "word" | "pdf"; onChange: (value: "word" | "pdf") => void }) {
  const { t } = useTranslation();
  const options = [
    { id: "word", icon: "description", label: t("viewer.asWord") },
    { id: "pdf", icon: "picture_as_pdf", label: t("viewer.asPdf") },
  ] as const;
  return <ViewToggle label={t("markdown.showAs")} options={options} value={value} onChange={onChange} />;
}

function ViewToggle<T extends string>({
  label,
  options,
  value,
  onChange,
}: {
  label: string;
  options: readonly { id: T; icon: string; label: string }[];
  value: T;
  onChange: (value: T) => void;
}) {
  return (
    <div role="radiogroup" aria-label={label} className="flex h-10 shrink-0 overflow-hidden rounded-full border border-outline">
      {options.map((option, i) => (
        <button
          key={option.id}
          type="button"
          role="radio"
          aria-checked={value === option.id}
          title={option.label}
          onClick={() => onChange(option.id)}
          className={cn(
            "inline-flex cursor-pointer items-center gap-1.5 px-3 text-label-lg max-sm:px-2.5",
            i > 0 && "border-l border-outline",
            value === option.id ? "bg-secondary-container text-on-secondary-container" : "text-on-surface-variant hover:bg-on-surface/8",
          )}
        >
          <Icon name={value === option.id ? "check" : option.icon} size="sm" />
          <span className="max-sm:sr-only">{option.label}</span>
        </button>
      ))}
    </div>
  );
}

/** A per-browser preference, such as wrapping long lines. Storage can be unavailable; then it lasts the page. */
function useStoredFlag(key: string) {
  const [value, setValue] = useState(() => {
    try {
      return localStorage.getItem(key) === "1";
    } catch {
      return false;
    }
  });
  const set = (next: boolean) => {
    setValue(next);
    try {
      localStorage.setItem(key, next ? "1" : "0");
    } catch {
      // Not remembered.
    }
  };
  return [value, set] as const;
}

/** An audio file's cover art, or a note when it has none, and its name, above the player's controls. */
function AudioCover({ name, src }: { name: string; src?: string }) {
  const { t } = useTranslation();
  const [failed, setFailed] = useState(false);
  return (
    <>
      {src && !failed ? (
        <img
          src={src}
          alt={t("viewer.cover")}
          onError={() => setFailed(true)}
          className="size-60 rounded-lg object-cover shadow-elevation-1 max-sm:size-32"
        />
      ) : (
        <span className="flex size-60 items-center justify-center rounded-lg bg-primary-container text-on-primary-container max-sm:size-32">
          <Icon name="music_note" filled size={96} />
        </span>
      )}
      <p className="m-0 max-w-full truncate text-title-md">{name}</p>
    </>
  );
}
