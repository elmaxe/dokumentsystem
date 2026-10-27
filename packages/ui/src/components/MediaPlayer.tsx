import { useEffect, useRef, useState, type KeyboardEvent, type PointerEvent, type ReactNode, type RefObject } from "react";
import { useTranslation } from "react-i18next";
import { cn } from "../lib/cn";
import { formatMs, leadInMs } from "../lib/media";
import { Icon } from "./Icon";
import type { TranscriptLineView } from "./Transcript";

const speeds = [1, 1.25, 1.5, 2, 0.75] as const;
const skipMs = 10_000;
const idleMs = 2_500;

export type MediaPlayerProps = {
  type: "video" | "audio";
  /** Tried in order: the first the browser can play is used, e.g. the original HEVC in Safari and the H.264 copy elsewhere. */
  sources: readonly string[];
  poster?: string;
  /** Above an audio file's controls: its cover art and name. */
  cover?: ReactNode;
  /** Shown when none of the sources play. */
  fallback: ReactNode;
  mediaRef: RefObject<HTMLMediaElement | null>;
  startAtMs?: number;
  onTime: (ms: number) => void;
  /** Seeks to a hit and plays from there; the header stepper and the ticks use it. */
  onSeek: (ms: number) => void;
  /** Where search hits are said, shown as ticks along the timeline. */
  ticks: readonly number[];
  /** The length until the browser knows it, from the server's probe. */
  durationMs?: number;
  /** A video's captions, drawn over the picture from the transcript. */
  captionLines?: readonly TranscriptLineView[];
  /** The same captions as WebVTT, for the phone's own full-screen player. */
  captions?: string;
  captionsLanguage?: string;
};

/**
 * The player for video and audio (§14, §11.1): our own controls instead of the browser's, so the hits from a search
 * show on the timeline itself and captions come from the transcript. Space or K plays and pauses, J and L skip 10 s,
 * the arrows 5 s, M mutes, C toggles captions and F goes full screen, while the player has focus.
 */
export function MediaPlayer({
  type,
  sources,
  poster,
  cover,
  fallback,
  mediaRef,
  startAtMs,
  onTime,
  onSeek,
  ticks,
  durationMs,
  captionLines,
  captions,
  captionsLanguage,
}: MediaPlayerProps) {
  const { t, i18n } = useTranslation();
  const [index, setIndex] = useState(0);
  const element = useRef<HTMLMediaElement | null>(null);
  const container = useRef<HTMLDivElement | null>(null);
  const [playing, setPlaying] = useState(false);
  const [waiting, setWaiting] = useState(false);
  const [nowMs, setNowMs] = useState(startAtMs ?? 0);
  const [loadedMs, setLoadedMs] = useState<number | null>(null);
  const [ratio, setRatio] = useState(16 / 9);
  const [bufferedMs, setBufferedMs] = useState(0);
  const [volume, setVolume] = useState(() => storedVolume());
  const [muted, setMuted] = useState(false);
  const [speed, setSpeed] = useState(1);
  const [captionsOn, setCaptionsOn] = useStoredSetting("dokument.player.captions");
  const [fullscreen, setFullscreen] = useState(false);
  const [active, setActive] = useState(true);
  const idle = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const hovering = useRef(false);
  const lengthMs = loadedMs ?? durationMs ?? 0;

  // Smooth progress while playing; timeupdate alone moves the thumb in jumps of a quarter second.
  useEffect(() => {
    if (!playing) return;
    let frame = requestAnimationFrame(function tick() {
      if (element.current) setNowMs(element.current.currentTime * 1000);
      frame = requestAnimationFrame(tick);
    });
    return () => cancelAnimationFrame(frame);
  }, [playing]);

  useEffect(() => {
    const onChange = () => setFullscreen(!!document.fullscreenElement && document.fullscreenElement === container.current);
    document.addEventListener("fullscreenchange", onChange);
    return () => document.removeEventListener("fullscreenchange", onChange);
  }, []);

  useEffect(() => () => clearTimeout(idle.current), []);

  if (index >= sources.length) return <>{fallback}</>;
  const src = sources[index];

  // The controls hide while a video plays and the pointer rests, like any player's.
  const wake = () => {
    setActive(true);
    clearTimeout(idle.current);
    idle.current = setTimeout(() => {
      if (!hovering.current) setActive(false);
    }, idleMs);
  };

  const media = () => element.current;
  const toggle = () => {
    const m = media();
    if (!m) return;
    if (m.paused || m.ended) void m.play().catch(() => {});
    else m.pause();
  };
  const scrubTo = (ms: number) => {
    const m = media();
    if (!m) return;
    const to = Math.min(Math.max(0, ms), lengthMs || ms);
    m.currentTime = to / 1000;
    setNowMs(to);
    onTime(to);
  };
  const skip = (ms: number) => scrubTo((media()?.currentTime ?? 0) * 1000 + ms);
  const changeVolume = (value: number) => {
    const m = media();
    if (!m) return;
    m.volume = value;
    m.muted = value === 0;
    try {
      localStorage.setItem("dokument.player.volume", String(value));
    } catch {
      // Not remembered.
    }
  };
  const toggleMute = () => {
    const m = media();
    if (!m) return;
    if (m.muted && m.volume === 0) m.volume = 1;
    m.muted = !m.muted;
  };
  const nextSpeed = () => {
    const m = media();
    if (!m) return;
    m.playbackRate = speeds[(speeds.indexOf(speed as (typeof speeds)[number]) + 1) % speeds.length]!;
  };
  const toggleFullscreen = () => {
    const box = container.current;
    const video = media() as (HTMLVideoElement & { webkitEnterFullscreen?: () => void }) | null;
    if (document.fullscreenElement) void document.exitFullscreen().catch(() => {});
    else if (box?.requestFullscreen) void box.requestFullscreen().catch(() => {});
    // iPhones only let the video itself go full screen, in their own player.
    else video?.webkitEnterFullscreen?.();
  };

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.altKey || event.ctrlKey || event.metaKey) return;
    const onButton = event.target instanceof HTMLButtonElement || event.target instanceof HTMLInputElement;
    const key = event.key.toLowerCase();
    const actions: Record<string, (() => void) | undefined> = {
      " ": onButton ? undefined : toggle,
      k: toggle,
      j: () => skip(-skipMs),
      l: () => skip(skipMs),
      arrowleft: onButton && event.target instanceof HTMLInputElement ? undefined : () => skip(-5_000),
      arrowright: onButton && event.target instanceof HTMLInputElement ? undefined : () => skip(5_000),
      m: toggleMute,
      c: type === "video" && captionLines?.length ? () => setCaptionsOn(!captionsOn) : undefined,
      f: type === "video" ? toggleFullscreen : undefined,
    };
    const action = actions[key];
    if (!action) return;
    event.preventDefault();
    // The viewer's own arrows move to the next file; while the player has focus they seek.
    event.stopPropagation();
    action();
    if (type === "video") wake();
  };

  const events = {
    onLoadedMetadata: () => {
      const m = media();
      if (!m) return;
      if (Number.isFinite(m.duration)) setLoadedMs(m.duration * 1000);
      if (m instanceof HTMLVideoElement && m.videoWidth && m.videoHeight) setRatio(m.videoWidth / m.videoHeight);
      if (startAtMs) m.currentTime = startAtMs / 1000;
      m.volume = volume;
    },
    onDurationChange: () => {
      const m = media();
      if (m && Number.isFinite(m.duration)) setLoadedMs(m.duration * 1000);
    },
    onTimeUpdate: () => {
      const ms = (media()?.currentTime ?? 0) * 1000;
      setNowMs(ms);
      onTime(ms);
    },
    onSeeked: () => {
      const ms = (media()?.currentTime ?? 0) * 1000;
      setNowMs(ms);
      onTime(ms);
    },
    onProgress: () => {
      const m = media();
      if (m && m.buffered.length > 0) setBufferedMs(m.buffered.end(m.buffered.length - 1) * 1000);
    },
    onPlay: () => {
      setPlaying(true);
      if (type === "video") wake();
    },
    onPause: () => {
      setPlaying(false);
      setActive(true);
    },
    onWaiting: () => setWaiting(true),
    onPlaying: () => setWaiting(false),
    onCanPlay: () => setWaiting(false),
    onVolumeChange: () => {
      const m = media();
      if (!m) return;
      setVolume(m.volume);
      setMuted(m.muted);
    },
    onRateChange: () => setSpeed(media()?.playbackRate ?? 1),
    onError: () => {
      setIndex((i) => i + 1);
      setPlaying(false);
    },
  };
  const attach = (node: HTMLMediaElement | null) => {
    element.current = node;
    mediaRef.current = node;
  };

  const silent = muted || volume === 0;
  const speedText = speed.toLocaleString(i18n.language);
  const scrubber = (
    <Scrubber
      nowMs={nowMs}
      lengthMs={lengthMs}
      bufferedMs={bufferedMs}
      ticks={ticks}
      onScrub={scrubTo}
      onTick={(time) => onSeek(time - leadInMs)}
      overlay={type === "video"}
    />
  );
  const speedButton = (
    <button
      type="button"
      onClick={nextSpeed}
      aria-label={t("player.speed", { speed: speedText })}
      title={t("player.speed", { speed: speedText })}
      className={cn(
        "tabular h-8 min-w-12 shrink-0 cursor-pointer rounded-full border px-3 text-label-lg focus-visible:outline-2",
        type === "video" ? "border-white/40 text-white hover:bg-white/10" : "border-outline text-on-surface hover:bg-on-surface/8",
      )}
    >
      {speedText}×
    </button>
  );
  const volumeControl = (
    <div className="group/volume flex items-center max-sm:hidden">
      <PlayerButton
        icon={silent ? "volume_off" : volume < 0.5 ? "volume_down" : "volume_up"}
        label={silent ? t("player.unmute") : t("player.mute")}
        onClick={toggleMute}
        overlay={type === "video"}
      />
      <input
        type="range"
        min={0}
        max={1}
        step={0.05}
        value={silent ? 0 : volume}
        onChange={(event) => changeVolume(Number(event.target.value))}
        aria-label={t("player.volume")}
        className={cn(
          "h-1 w-20 cursor-pointer",
          type === "video" ? "accent-white" : "accent-primary",
          "transition-[width,opacity] group-hover/volume:w-20 group-hover/volume:opacity-100 focus-visible:w-20 focus-visible:opacity-100 pointer-fine:w-0 pointer-fine:opacity-0",
        )}
      />
    </div>
  );

  if (type === "audio") {
    return (
      <div
        ref={container}
        onKeyDown={onKeyDown}
        className="flex w-full max-w-xl flex-col items-center gap-6 px-6 py-8 max-sm:gap-4 max-sm:py-4"
      >
        <audio key={src} ref={attach} src={src} autoPlay preload="metadata" {...events} />
        {cover}
        <div className="flex w-full flex-col gap-1">
          {scrubber}
          <div className="tabular flex justify-between text-body-sm text-on-surface-variant">
            <span>{formatMs(nowMs)}</span>
            <span>{lengthMs ? formatMs(lengthMs) : "–:––"}</span>
          </div>
        </div>
        <div className="grid w-full grid-cols-[1fr_auto_1fr] items-center gap-4">
          <div className="flex justify-start">{volumeControl}</div>
          <div className="flex items-center gap-5">
            <PlayerButton icon="replay_10" label={t("player.back")} onClick={() => skip(-skipMs)} />
            <button
              type="button"
              onClick={toggle}
              aria-label={playing ? t("player.pause") : t("player.play")}
              className="flex size-16 shrink-0 cursor-pointer items-center justify-center rounded-full bg-primary text-on-primary shadow-elevation-1 hover:shadow-elevation-2 focus-visible:outline-2 focus-visible:outline-offset-2"
            >
              {waiting && playing ? <Spinner /> : <Icon name={playing ? "pause" : "play_arrow"} filled size={36} />}
            </button>
            <PlayerButton icon="forward_10" label={t("player.forward")} onClick={() => skip(skipMs)} />
          </div>
          <div className="flex justify-end">{speedButton}</div>
        </div>
      </div>
    );
  }

  const caption = captionsOn && captionLines ? captionLines.find((l) => l.startMs <= nowMs && nowMs < l.endMs) : undefined;
  const shown = active || !playing;
  return (
    <div
      ref={container}
      onKeyDown={onKeyDown}
      onPointerMove={wake}
      onPointerDown={wake}
      // A size container, so the picture can grow to the largest box of its shape that fits, controls on top of it.
      style={{ containerType: "size" }}
      className={cn("flex size-full items-center justify-center", fullscreen ? "bg-black" : "sm:p-6")}
    >
      <div
        style={{ aspectRatio: ratio, width: `min(100cqw, ${ratio} * 100cqh)` }}
        className={cn(
          "relative overflow-hidden bg-black",
          !fullscreen && "shadow-elevation-2 sm:rounded-sm",
          !shown && "cursor-none",
        )}
      >
      <video
        key={src}
        ref={attach}
        src={src}
        poster={poster}
        autoPlay
        playsInline
        preload="metadata"
        onClick={toggle}
        onDoubleClick={toggleFullscreen}
        {...events}
        className="block size-full object-contain"
      >
        {captions && <track kind="captions" src={captions} srcLang={captionsLanguage} label={t("transcript.captions")} />}
      </video>
      {waiting && playing && (
        <div className="pointer-events-none absolute inset-0 flex items-center justify-center text-white">
          <Spinner large />
        </div>
      )}
      {!playing && !waiting && (
        <button
          type="button"
          onClick={toggle}
          aria-label={t("player.play")}
          className="absolute top-1/2 left-1/2 flex size-16 -translate-1/2 cursor-pointer items-center justify-center rounded-full bg-black/60 text-white hover:bg-black/75 focus-visible:outline-2 focus-visible:outline-white"
        >
          <Icon name="play_arrow" filled size={40} />
        </button>
      )}
      {caption && (
        <div
          className={cn(
            "pointer-events-none absolute inset-x-0 flex justify-center px-6 transition-[bottom]",
            shown ? "bottom-24" : "bottom-6",
          )}
        >
          <p
            lang={captionsLanguage}
            className="m-0 max-w-[80%] rounded-sm bg-black/75 px-3 py-1 text-center text-title-md text-pretty text-white max-sm:text-body-md"
          >
            {caption.text}
          </p>
        </div>
      )}
      <div
        onPointerEnter={() => (hovering.current = true)}
        onPointerLeave={() => (hovering.current = false)}
        onFocus={() => setActive(true)}
        className={cn(
          "absolute inset-x-0 bottom-0 flex flex-col gap-1 bg-linear-to-t from-black/80 via-black/50 to-transparent px-3 pt-10 pb-2 text-white transition-opacity duration-200",
          shown ? "opacity-100" : "pointer-events-none opacity-0",
        )}
      >
        {scrubber}
        <div className="flex items-center gap-1">
          <PlayerButton icon={playing ? "pause" : "play_arrow"} label={playing ? t("player.pause") : t("player.play")} onClick={toggle} overlay filled />
          <PlayerButton icon="replay_10" label={t("player.back")} onClick={() => skip(-skipMs)} overlay className="max-sm:hidden" />
          <PlayerButton icon="forward_10" label={t("player.forward")} onClick={() => skip(skipMs)} overlay className="max-sm:hidden" />
          {volumeControl}
          <span className="tabular px-2 text-body-sm whitespace-nowrap">
            {formatMs(nowMs)} / {lengthMs ? formatMs(lengthMs) : "–:––"}
          </span>
          <span className="flex-1" />
          {speedButton}
          {!!captionLines?.length && (
            <PlayerButton
              icon={captionsOn ? "closed_caption" : "closed_caption_disabled"}
              label={t("player.captions")}
              pressed={captionsOn}
              onClick={() => setCaptionsOn(!captionsOn)}
              overlay
              filled={captionsOn}
            />
          )}
          <PlayerButton
            icon={fullscreen ? "fullscreen_exit" : "fullscreen"}
            label={fullscreen ? t("player.exitFullscreen") : t("player.fullscreen")}
            onClick={toggleFullscreen}
            overlay
          />
        </div>
      </div>
      </div>
    </div>
  );
}

/**
 * The timeline: what's played, what's loaded, a tick per hit, and the time under the pointer. Drag or click to seek;
 * as a slider it takes the arrow keys (5 s), Page Up and Down (1 min), Home and End.
 */
function Scrubber({
  nowMs,
  lengthMs,
  bufferedMs,
  ticks,
  onScrub,
  onTick,
  overlay,
}: {
  nowMs: number;
  lengthMs: number;
  bufferedMs: number;
  ticks: readonly number[];
  onScrub: (ms: number) => void;
  onTick: (ms: number) => void;
  overlay?: boolean;
}) {
  const { t } = useTranslation();
  const bar = useRef<HTMLDivElement | null>(null);
  const [dragMs, setDragMs] = useState<number | null>(null);
  const [hover, setHover] = useState<{ ms: number; x: number } | null>(null);
  const shownMs = dragMs ?? nowMs;
  // Over a video the controls are dark in both themes, so the light theme's primary would be too dark there.
  const played = overlay ? "bg-primary-container dark:bg-primary" : "bg-primary";
  const percent = (ms: number) => (lengthMs ? `${Math.min(100, Math.max(0, (ms / lengthMs) * 100))}%` : "0%");
  const msAt = (event: PointerEvent<HTMLDivElement>) => {
    const box = bar.current!.getBoundingClientRect();
    const x = Math.min(Math.max(0, event.clientX - box.left), box.width);
    return { ms: box.width ? (x / box.width) * lengthMs : 0, x };
  };

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    const steps: Record<string, number> = {
      ArrowLeft: nowMs - 5_000,
      ArrowDown: nowMs - 5_000,
      ArrowRight: nowMs + 5_000,
      ArrowUp: nowMs + 5_000,
      PageDown: nowMs - 60_000,
      PageUp: nowMs + 60_000,
      Home: 0,
      End: lengthMs,
    };
    const to = steps[event.key];
    if (to === undefined) return;
    event.preventDefault();
    event.stopPropagation();
    onScrub(to);
  };

  return (
    <div className="relative h-6 touch-none select-none">
      <div
        ref={bar}
        role="slider"
        tabIndex={0}
        aria-label={t("player.position")}
        aria-valuemin={0}
        aria-valuemax={Math.round(lengthMs / 1000)}
        aria-valuenow={Math.round(shownMs / 1000)}
        aria-valuetext={t("player.positionText", { at: formatMs(shownMs), total: formatMs(lengthMs) })}
        onKeyDown={onKeyDown}
        onPointerDown={(event) => {
          if (!lengthMs) return;
          event.currentTarget.setPointerCapture(event.pointerId);
          setDragMs(msAt(event).ms);
        }}
        onPointerMove={(event) => {
          if (!lengthMs) return;
          const at = msAt(event);
          if (dragMs !== null) setDragMs(at.ms);
          if (event.pointerType === "mouse" || dragMs !== null) setHover(at);
        }}
        onPointerUp={() => {
          if (dragMs !== null) onScrub(dragMs);
          setDragMs(null);
        }}
        onPointerCancel={() => setDragMs(null)}
        onPointerLeave={() => setHover(null)}
        className="group/scrub absolute inset-0 cursor-pointer rounded-full focus-visible:outline-2 focus-visible:outline-offset-2"
      >
        <div className={cn("absolute inset-x-0 top-1/2 h-1 -translate-y-1/2 rounded-full group-hover/scrub:h-1.5", overlay ? "bg-white/30" : "bg-outline-variant")}>
          <div className={cn("absolute inset-y-0 left-0 rounded-full", overlay ? "bg-white/40" : "bg-on-surface/15")} style={{ width: percent(bufferedMs) }} />
          <div className={cn("absolute inset-y-0 left-0 rounded-full", played)} style={{ width: percent(shownMs) }} />
        </div>
        <div
          className={cn(
            "absolute top-1/2 size-3.5 -translate-1/2 rounded-full shadow-elevation-1 transition-transform",
            played,
            dragMs !== null ? "scale-125" : "group-hover/scrub:scale-110",
          )}
          style={{ left: percent(shownMs) }}
        />
      </div>
      {lengthMs > 0 &&
        ticks.map((time) => (
          // The header's stepper is the keyboard's way to the hits, so the ticks are for the pointer only.
          <button
            key={time}
            type="button"
            tabIndex={-1}
            aria-hidden
            title={formatMs(time)}
            onPointerDown={(event) => event.stopPropagation()}
            onClick={() => onTick(time)}
            style={{ left: percent(time) }}
            className={cn(
              "absolute top-1/2 h-4 w-1 -translate-1/2 cursor-pointer rounded-full ring-1 ring-black/30 hover:scale-125",
              overlay ? "bg-warning-container dark:bg-warning" : "bg-warning",
            )}
          />
        ))}
      {hover && (
        <span
          className="tabular pointer-events-none absolute bottom-full mb-1 -translate-x-1/2 rounded-xs bg-on-surface px-1.5 py-0.5 text-label-md text-surface"
          style={{ left: hover.x }}
        >
          {formatMs(hover.ms)}
        </span>
      )}
    </div>
  );
}

function PlayerButton({
  icon,
  label,
  onClick,
  overlay,
  filled,
  pressed,
  className,
}: {
  icon: string;
  label: string;
  onClick: () => void;
  overlay?: boolean;
  filled?: boolean;
  pressed?: boolean;
  className?: string;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-label={label}
      aria-pressed={pressed}
      title={label}
      className={cn(
        "flex size-10 shrink-0 cursor-pointer items-center justify-center rounded-full focus-visible:outline-2 pointer-coarse:size-11",
        overlay ? "text-white hover:bg-white/15 focus-visible:outline-white" : "text-on-surface hover:bg-on-surface/8",
        className,
      )}
    >
      <Icon name={icon} filled={filled} />
    </button>
  );
}

function Spinner({ large }: { large?: boolean }) {
  return (
    <span
      aria-hidden
      className={cn("animate-spin rounded-full border-current border-t-transparent", large ? "size-12 border-4" : "size-7 border-3")}
    />
  );
}

function storedVolume() {
  try {
    const value = Number(localStorage.getItem("dokument.player.volume") ?? 1);
    return Number.isFinite(value) && value >= 0 && value <= 1 ? value : 1;
  } catch {
    return 1;
  }
}

function useStoredSetting(key: string) {
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
