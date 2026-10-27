import type { TFunction } from "i18next";

/** A step of the worker's pipeline (docs/design.md §10.2), in the order it runs. */
export type ProcessingStepName = "thumbnail" | "metadata" | "text";

/** A step that is still waiting or running, as file lists show it. */
export type ActiveProcessingStep = { step: ProcessingStepName; running: boolean };

export type ProcessingState =
  | "notStarted"
  | "waiting"
  | "paused"
  | "running"
  | "done"
  | "nothingFound"
  | "skipped"
  | "unsupported"
  | "unreadable"
  | "failed";

/** One step in the details panel. `jobId`, `attempts` and `error` are only there for admins. */
export type ProcessingStepView = {
  step: ProcessingStepName;
  state: ProcessingState;
  /** ISO 8601: when it started, finished or gave up. */
  at?: string | null;
  /** For a waiting step: how many jobs of its type run first. */
  ahead?: number | null;
  jobId?: string | null;
  attempts?: number | null;
  error?: string | null;
};

/** "Thumbnail: running · Searchable text: waiting", for the badge's tooltip and label. */
export function processingLabel(t: TFunction, steps: ActiveProcessingStep[]): string {
  return steps
    .map((s) =>
      t("processing.stepState", {
        step: t(`processing.steps.${s.step}`),
        state: t(s.running ? "processing.running" : "processing.waiting"),
      }),
    )
    .join(" · ");
}
