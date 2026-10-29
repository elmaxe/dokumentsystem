import { formatDuration } from "@dokument/core";

/** Opened at a hit, a recording starts this long before it, so the sentence is heard from its start (§11.1). */
export const leadInMs = 10_000;

/** "12:34" for a time in milliseconds. */
export const formatMs = (ms: number) => formatDuration(Math.max(0, ms) / 1000);
