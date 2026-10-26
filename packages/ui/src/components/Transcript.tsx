import { findHits } from "@dokument/core";
import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { cn } from "../lib/cn";
import { formatMs } from "../lib/media";
import { IconButton } from "./Button";
import { Highlighted } from "./Highlighted";
import { Icon } from "./Icon";
import { Switch } from "./Switch";

export type TranscriptLineView = { startMs: number; endMs: number; text: string };

/** What's said in a recording, as the viewer shows it (docs/design.md §11.1). */
export type TranscriptView = {
  state: "waiting" | "running" | "ready" | "noSpeech" | "failed";
  /** `sv` or `en`, as recognized. */
  language?: string | null;
  lines: readonly TranscriptLineView[];
  durationMs?: number | null;
  /** While running: how far it has got. */
  doneMs?: number | null;
};

/** A stretch this long without words is shown as "No speech", so a jump in the times isn't a surprise. */
const silenceMs = 30_000;


export type TranscriptPanelProps = {
  transcript: TranscriptView;
  /** Where the player is, to mark the line being said and follow along. */
  currentMs: number;
  /** The search terms to mark, when the file was opened from a hit. */
  terms?: readonly string[];
  onSeek: (ms: number) => void;
  onClose: () => void;
  className?: string;
};

/**
 * The transcript beside the player (§11.1): every line with its time, the one being said marked and kept in view
 * while "Follow along" is on, a click plays from a line, and a find box marks other words.
 */
export function TranscriptPanel({ transcript, currentMs, terms = [], onSeek, onClose, className }: TranscriptPanelProps) {
  const { t } = useTranslation();
  const [follow, setFollow] = useState(true);
  const [find, setFind] = useState("");
  const list = useRef<HTMLOListElement>(null);
  const { lines, state } = transcript;
  const findTerms = find.trim() ? find.trim().split(/\s+/) : [];
  const marked = findTerms.length > 0 ? findTerms : terms;
  const found = findTerms.length > 0 ? lines.filter((l) => findHits(l.text, findTerms).length > 0).length : null;
  let active = -1;
  for (let i = 0; i < lines.length && lines[i]!.startMs <= currentMs; i++) active = i;

  useEffect(() => {
    if (!follow || active < 0) return;
    list.current
      ?.querySelector(`[data-line="${active}"]`)
      ?.scrollIntoView({ block: "center", behavior: window.matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth" });
  }, [active, follow]);

  const language = transcript.language === "sv" || transcript.language === "en" ? t(`transcript.languages.${transcript.language}`) : null;
  return (
    <aside
      aria-label={t("transcript.title")}
      className={cn("flex min-h-0 flex-col border-outline-variant bg-surface-container-low text-on-surface", className)}
    >
      <div className="flex h-14 shrink-0 items-center gap-2 pr-1 pl-4">
        <h2 className="m-0 flex-1 text-title-sm">{t("transcript.title")}</h2>
        {lines.length > 0 && (
          <label className="flex items-center gap-2 text-label-lg text-on-surface-variant">
            {t("transcript.follow")}
            <Switch checked={follow} onCheckedChange={setFollow} label={t("transcript.follow")} />
          </label>
        )}
        <IconButton icon="close" label={t("transcript.hide")} size="sm" onClick={onClose} />
      </div>
      {lines.length > 0 && (
        <div className="flex shrink-0 flex-col gap-2 px-4 pb-2">
          <label className="flex h-10 items-center gap-2 rounded-xl bg-surface-container-high px-3 text-body-md">
            <Icon name="search" size="sm" className="text-on-surface-variant" />
            <input
              type="search"
              value={find}
              onChange={(event) => setFind(event.target.value)}
              placeholder={t("transcript.find")}
              aria-label={t("transcript.find")}
              className="min-w-0 flex-1 bg-transparent outline-none placeholder:text-on-surface-variant"
            />
            {found !== null && <span className="tabular text-label-md text-on-surface-variant">{t("transcript.found", { count: found })}</span>}
          </label>
          <p className="m-0 text-body-sm text-on-surface-variant">
            {[language && t("transcript.recognized", { language }), t("transcript.misheard")].filter(Boolean).join(" · ")}
          </p>
        </div>
      )}
      <ol ref={list} className="m-0 min-h-0 flex-1 list-none overflow-y-auto px-2 pb-4">
        {lines.map((line, i) => {
          const gap = i > 0 ? line.startMs - lines[i - 1]!.endMs : line.startMs;
          return (
            <li key={line.startMs} data-line={i}>
              {gap >= silenceMs && (
                <p className="tabular m-0 px-2 py-1 text-body-sm text-on-surface-variant italic">
                  {t("transcript.silence", { from: formatMs(line.startMs - gap), to: formatMs(line.startMs) })}
                </p>
              )}
              <button
                type="button"
                onClick={() => onSeek(line.startMs)}
                aria-current={i === active ? "true" : undefined}
                className={cn(
                  "flex w-full cursor-pointer gap-3 rounded-sm px-2 py-1.5 text-left text-body-md hover:bg-on-surface/8 focus-visible:outline-2",
                  i === active && "bg-secondary-container text-on-secondary-container",
                )}
              >
                <span className="tabular w-12 shrink-0 pt-px text-label-md text-on-surface-variant">{formatMs(line.startMs)}</span>
                <span className="min-w-0 flex-1">
                  <Highlighted text={line.text} terms={marked} />
                </span>
              </button>
            </li>
          );
        })}
        {state !== "ready" && (
          <li className="flex items-start gap-3 px-2 py-3 text-body-md text-on-surface-variant">
            <Icon
              name={state === "failed" ? "error" : state === "noSpeech" ? "voice_over_off" : "hourglass_top"}
              size="sm"
              className="mt-0.5 shrink-0"
            />
            <span>
              {state === "running" && transcript.doneMs != null && transcript.durationMs
                ? t("transcript.running", { at: formatMs(transcript.doneMs), total: formatMs(transcript.durationMs) })
                : t(`transcript.states.${state}`)}
            </span>
          </li>
        )}
      </ol>
    </aside>
  );
}
