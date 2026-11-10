import { formatDuration, seekFrame, type SeekSheet } from "@dokument/core";

/** Opened at a hit, a recording starts this long before it, so the sentence is heard from its start (§11.1). */
export const leadInMs = 10_000;

/** "12:34" for a time in milliseconds. */
export const formatMs = (ms: number) => formatDuration(Math.max(0, ms) / 1000);

/** A video's seek previews (§14) and where its sheet loads from. */
export type SeekPreviews = SeekSheet & { src: string };

/** Where in the sheet the frame for `ms` is, and its size, scaled to fit a `boxWidth` × `boxHeight` box. */
export function seekFrameAt(sheet: SeekPreviews, ms: number, boxWidth: number, boxHeight: number) {
  const frame = seekFrame(sheet, ms, boxWidth, boxHeight);
  return {
    index: frame.index,
    width: frame.width,
    height: frame.height,
    backgroundSize: `${frame.sheetWidth}px ${frame.sheetHeight}px`,
    backgroundPosition: `${frame.x}px ${frame.y}px`,
  };
}
