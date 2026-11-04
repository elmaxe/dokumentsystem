import { formatDuration } from "@dokument/core";

/** Opened at a hit, a recording starts this long before it, so the sentence is heard from its start (§11.1). */
export const leadInMs = 10_000;

/** "12:34" for a time in milliseconds. */
export const formatMs = (ms: number) => formatDuration(Math.max(0, ms) / 1000);

/**
 * A video's seek previews (§14): one image of `count` frames, `columns` to a row, left to right and top to bottom.
 * Frame `i` stands for the time from `i × intervalMs` up to the next one.
 */
export type SeekPreviews = {
  src: string;
  intervalMs: number;
  count: number;
  columns: number;
  frameWidth: number;
  frameHeight: number;
};

/** Where in the sheet the frame for `ms` is, and its size, scaled to fit a `boxWidth` × `boxHeight` box. */
export function seekFrameAt(sheet: SeekPreviews, ms: number, boxWidth: number, boxHeight: number) {
  const index = Math.min(sheet.count - 1, Math.max(0, Math.floor(ms / sheet.intervalMs)));
  const scale = Math.min(boxWidth / sheet.frameWidth, boxHeight / sheet.frameHeight);
  const width = Math.round(sheet.frameWidth * scale);
  const height = Math.round(sheet.frameHeight * scale);
  const rows = Math.ceil(sheet.count / sheet.columns);
  return {
    index,
    width,
    height,
    backgroundSize: `${sheet.columns * width}px ${rows * height}px`,
    backgroundPosition: `${-(index % sheet.columns) * width}px ${-Math.floor(index / sheet.columns) * height}px`,
  };
}
