import { useState } from "react";
import { useTranslation } from "react-i18next";
import { cn } from "../lib/cn";
import { useFormat } from "../lib/display";
import {
  processingLabel,
  type ActiveProcessingStep,
  type ProcessingState,
  type ProcessingStepView,
} from "../lib/processing";
import { Button } from "./Button";
import { Icon } from "./Icon";

function Spinner({ size = 16, className }: { size?: number; className?: string }) {
  return (
    <span aria-hidden className={cn("inline-flex motion-safe:animate-spin", className)}>
      <Icon name="progress_activity" size={size} />
    </span>
  );
}

/** The pill on a grid tile's thumbnail while the worker still has steps to do for the file (§16.4). */
export function ProcessingBadge({ steps, className }: { steps: ActiveProcessingStep[]; className?: string }) {
  const { t } = useTranslation();
  const label = processingLabel(t, steps);
  return (
    <span
      role="img"
      title={label}
      aria-label={t("processing.badgeLabel", { steps: label })}
      className={cn(
        "inline-flex h-7 items-center gap-1.5 rounded-full bg-surface-container-lowest/95 pr-2.5 pl-1.5 text-label-md text-on-surface-variant shadow-elevation-1",
        className,
      )}
    >
      <Spinner className="text-primary" />
      {t("processing.badge")}
    </span>
  );
}

/** The same, as a spinner after the name in list rows. */
export function ProcessingIcon({ steps }: { steps: ActiveProcessingStep[] }) {
  const { t } = useTranslation();
  const label = processingLabel(t, steps);
  return (
    <span role="img" title={label} aria-label={t("processing.badgeLabel", { steps: label })} className="inline-flex shrink-0">
      <Spinner size={18} className="text-primary" />
    </span>
  );
}

const icons: Record<ProcessingState, { name: string; filled?: boolean; className: string }> = {
  notStarted: { name: "schedule", className: "text-on-surface-variant" },
  waiting: { name: "hourglass_top", className: "text-on-surface-variant" },
  paused: { name: "pause_circle", className: "text-warning" },
  running: { name: "progress_activity", className: "text-primary" },
  done: { name: "check_circle", filled: true, className: "text-success" },
  nothingFound: { name: "check_circle", filled: true, className: "text-success" },
  skipped: { name: "block", className: "text-on-surface-variant" },
  unsupported: { name: "remove_circle_outline", className: "text-on-surface-variant" },
  unreadable: { name: "error_outline", className: "text-on-surface-variant" },
  failed: { name: "error", filled: true, className: "text-error" },
};

/** States with nothing more to happen. */
const settled: ProcessingState[] = ["done", "nothingFound", "skipped", "unsupported", "unreadable"];

export type ProcessingSectionProps = {
  steps: ProcessingStepView[];
  /** Admins only: runs a failed step again. */
  onRetry?: (jobId: string) => void;
};

/** The Processing part of a file's details: where the worker is with each step (§10.1). */
export function ProcessingSection({ steps, onRetry }: ProcessingSectionProps) {
  const { t } = useTranslation();
  const format = useFormat();
  const [expanded, setExpanded] = useState(false);
  const when = (at?: string | null) => (at ? format.date(at, { inSentence: true }) : "");

  // Once every step is settled there is nothing to watch, so the section folds to one line (§16.4).
  if (!expanded && steps.length > 0 && steps.every((s) => settled.includes(s.state))) {
    return (
      <section className="mt-2 border-t border-outline-variant pt-2">
        <button
          type="button"
          aria-expanded={false}
          onClick={() => setExpanded(true)}
          className="flex min-h-10 w-full cursor-pointer items-center gap-3 rounded-sm text-left hover:bg-on-surface/4"
        >
          <Icon name="check_circle" filled size={20} className="shrink-0 text-success" />
          <span className="flex-1 text-title-sm text-on-surface">
            {t("processing.title")}
            <span className="text-body-sm text-on-surface-variant"> · {t("processing.allDone")}</span>
          </span>
          <Icon name="expand_more" className="text-on-surface-variant" />
        </button>
      </section>
    );
  }

  const describe = (s: ProcessingStepView) => {
    switch (s.state) {
      case "running":
        return s.at ? t("processing.state.runningSince", { time: when(s.at) }) : t("processing.state.running");
      case "waiting":
        return s.ahead ? t("processing.state.waiting", { count: s.ahead }) : t("processing.state.next");
      case "failed":
        return s.error && s.at
          ? t("processing.state.gaveUp", { count: s.attempts ?? 0, time: when(s.at) })
          : t("processing.state.failed");
      case "done":
      case "nothingFound":
        return t(`processing.state.${s.state}`, { time: when(s.at) });
      default:
        return t(`processing.state.${s.state}`);
    }
  };

  return (
    <section aria-labelledby="details-processing" className="mt-2 flex flex-col gap-3 border-t border-outline-variant pt-4">
      <h3 id="details-processing" className="m-0 text-title-sm text-on-surface">
        {t("processing.title")}
      </h3>
      <ul className="m-0 flex list-none flex-col gap-3 p-0">
        {steps.map((s) => {
          const icon = icons[s.state];
          const searchWaits = s.step === "text" && ["waiting", "paused", "running", "notStarted"].includes(s.state);
          return (
            <li key={s.step} className="flex gap-3">
              {s.state === "running" ? (
                <Spinner size={20} className={cn("h-5 shrink-0 self-start", icon.className)} />
              ) : (
                <Icon name={icon.name} filled={icon.filled} size={20} className={cn("shrink-0", icon.className)} />
              )}
              <div className="flex min-w-0 flex-col gap-0.5">
                <span className="text-body-md text-on-surface">{t(`processing.steps.${s.step}`)}</span>
                <span className="text-body-sm text-on-surface-variant">
                  {describe(s)}
                  {searchWaits && ` ${t("processing.searchWaits")}`}
                </span>
                {s.state === "failed" && s.error && (
                  <code className="mt-1 rounded-xs bg-surface-container-high px-1.5 py-1 font-mono text-mono-sm break-words text-on-surface-variant">
                    {s.error}
                  </code>
                )}
                {s.state === "failed" && s.jobId && onRetry && (
                  <Button variant="outlined" icon="replay" onClick={() => onRetry(s.jobId!)} className="mt-1 self-start">
                    {t("processing.retry")}
                  </Button>
                )}
              </div>
            </li>
          );
        })}
      </ul>
    </section>
  );
}
