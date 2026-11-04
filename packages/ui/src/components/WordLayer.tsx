import { wordsToText, type PageWord } from "@dokument/core";
import { use, useEffect, useRef, useState, type ClipboardEvent } from "react";
import { useTranslation } from "react-i18next";
import { cn } from "../lib/cn";
import { SnackbarContext } from "../lib/snackbar";
import { IconButton } from "./Button";
import { Icon } from "./Icon";

const measure = typeof document === "undefined" ? null : document.createElement("canvas").getContext("2d");
const font = "ui-sans-serif, system-ui, sans-serif";

/** How wide a word is in the layer's font at a size, to stretch it over its box. */
function naturalWidth(text: string, size: number): number | undefined {
  if (!measure) return undefined;
  measure.font = `${size}px ${font}`;
  return measure.measureText(text).width;
}

/**
 * The words on a page, shown by the viewer's Text toggle (docs/design.md §14): each in a soft box over the picture,
 * holding the word as invisible text stretched to fit, so it can be selected with the mouse or a long press and copied.
 * Drawn under the search marks.
 */
export function WordLayer({ words }: { words: readonly PageWord[] }) {
  const layer = useRef<HTMLSpanElement>(null);
  const [size, setSize] = useState<{ width: number; height: number } | null>(null);

  useEffect(() => {
    const element = layer.current;
    if (!element) return;
    const observer = new ResizeObserver(([entry]) => entry && setSize({ width: entry.contentRect.width, height: entry.contentRect.height }));
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  // Copies whole lines as they read, with spaces between words and line breaks between lines, rather than the
  // browser's guess from boxes placed anywhere.
  const copy = (event: ClipboardEvent) => {
    const selection = window.getSelection();
    if (!selection || selection.rangeCount === 0 || !layer.current) return;
    const range = selection.getRangeAt(0);
    const spans = [...layer.current.querySelectorAll<HTMLElement>("[data-word]")];
    const picked = spans.flatMap((span) => {
      if (!selection.containsNode(span, true)) return [];
      const word = words[Number(span.dataset.word)];
      if (!word) return [];
      const text = span.textContent ?? "";
      const start = span.contains(range.startContainer) ? range.startOffset : 0;
      const end = span.contains(range.endContainer) ? range.endOffset : text.length;
      return [{ word, text: text.slice(start, end) }];
    });
    if (picked.length === 0) return;
    event.preventDefault();
    event.clipboardData.setData("text/plain", wordsToText(picked));
  };

  return (
    <span ref={layer} data-word-layer className="absolute inset-0 cursor-text overflow-hidden" onCopy={copy}>
      {size &&
        words.map((word, i) => {
          const height = word.height * size.height;
          const width = word.width * size.width;
          const natural = naturalWidth(word.text, height) || width;
          return (
            <span
              key={i}
              data-word={i}
              // A word starts a selection rather than panning or zooming the picture.
              onPointerDown={(event) => event.stopPropagation()}
              onDoubleClick={(event) => event.stopPropagation()}
              className="absolute rounded-[3px] bg-primary/12 whitespace-pre text-transparent outline-1 outline-primary/55 select-text selection:bg-primary/40 selection:text-transparent"
              style={{ left: word.x * size.width, top: word.y * size.height, width, height }}
            >
              <span
                className="absolute top-0 left-0 origin-top-left leading-none"
                style={{ fontFamily: font, fontSize: height, transform: `scaleX(${width / natural})` }}
              >
                {word.text}
              </span>
            </span>
          );
        })}
    </span>
  );
}

/** The viewer's Text toggle (§14), for a preview with words to show. */
export type ViewerText = {
  shown: boolean;
  onShownChange: (shown: boolean) => void;
  /** The words the server recognized on a page, or null where it read none (a PDF then uses its own text). */
  wordsOn: (page: number) => Promise<readonly PageWord[]> | null;
};

/** Loads a page's words while the text is shown: null while loading or hidden, empty when the page has none. */
export function useWords(load: (() => Promise<readonly PageWord[]>) | null, key: string): readonly PageWord[] | null {
  const [words, setWords] = useState<{ key: string; words: readonly PageWord[] } | null>(null);
  useEffect(() => {
    if (!load) return;
    let cancelled = false;
    load().then(
      (found) => !cancelled && setWords({ key, words: found }),
      () => !cancelled && setWords({ key, words: [] }),
    );
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- keyed by the page, not the loader's identity
  }, [key, !!load]);
  return load && words?.key === key ? words.words : null;
}

/**
 * The Text toggle and Copy at the end of the viewer's toolbar, on wider screens. Phones get the toggle as a round
 * button above the toolbar instead ({@link TextFloatingToggle}), and copy through their own selection menu.
 */
export function TextControls({ text, words, page }: { text: ViewerText; words: readonly PageWord[] | null; page?: boolean }) {
  const { t } = useTranslation();
  const toast = use(SnackbarContext);
  const copyAll = async () => {
    try {
      await navigator.clipboard.writeText(wordsToText((words ?? []).map((word) => ({ word }))));
      toast?.(t("viewer.textCopied"));
    } catch {
      toast?.(t("viewer.copyFailed"));
    }
  };
  return (
    <>
      <span className="mx-1 h-6 w-px bg-outline-variant max-sm:hidden" />
      <button
        type="button"
        aria-pressed={text.shown}
        title={text.shown ? t("viewer.hideText") : t("viewer.showText")}
        onClick={() => text.onShownChange(!text.shown)}
        className={cn(
          "flex h-8 items-center gap-2 rounded-full pr-3 pl-2 text-label-lg max-sm:hidden",
          text.shown ? "bg-primary-container text-on-primary-container" : "text-on-surface hover:bg-on-surface/8",
        )}
      >
        <Icon name="document_scanner" filled={text.shown} size="sm" />
        {t("viewer.text")}
      </button>
      {text.shown && (
        <IconButton
          icon="content_copy"
          label={page ? t("viewer.copyPageText") : t("viewer.copyText")}
          size="sm"
          disabled={!words?.length}
          onClick={() => void copyAll()}
          className="max-sm:hidden"
        />
      )}
    </>
  );
}

/** The Text toggle on phones: a round button above the toolbar, at the right, like the camera apps' text buttons. */
export function TextFloatingToggle({ text }: { text: ViewerText }) {
  const { t } = useTranslation();
  return (
    <IconButton
      icon="document_scanner"
      label={text.shown ? t("viewer.hideText") : t("viewer.showText")}
      selected={text.shown}
      onClick={() => text.onShownChange(!text.shown)}
      className={cn(
        "absolute right-4 bottom-20 size-12 shadow-elevation-2 pointer-coarse:size-12 sm:hidden",
        text.shown ? "bg-primary-container text-on-primary-container" : "bg-surface-container",
      )}
    />
  );
}

/** Said over the preview when the text is shown but the page has none, such as a PDF page that is only a picture. */
export function NoTextNote() {
  const { t } = useTranslation();
  return (
    <span
      role="status"
      className="pointer-events-none absolute bottom-20 left-1/2 flex -translate-x-1/2 items-center gap-2 rounded-full bg-inverse-surface px-4 py-2 text-body-md whitespace-nowrap text-inverse-on-surface max-sm:bottom-36"
    >
      <Icon name="info" size="sm" />
      {t("viewer.noTextHere")}
    </span>
  );
}
