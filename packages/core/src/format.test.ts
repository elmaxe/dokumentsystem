import { describe, expect, it } from "vitest";
import { formatBytes, formatDuration } from "./format";

const nbsp = " ";

describe("formatBytes", () => {
  it.each([
    [0, "en", `0${nbsp}B`],
    [1023, "en", `1,023${nbsp}B`],
    [1024, "en", `1.0${nbsp}KiB`],
    [1_468_006, "en", `1.4${nbsp}MiB`],
    [1_468_006, "sv", `1,4${nbsp}MiB`],
    [52 * 1024 ** 2, "sv", `52${nbsp}MiB`],
    [50 * 1024 ** 3, "en", `50${nbsp}GiB`],
    [3.2 * 1024 ** 4, "en", `3.2${nbsp}TiB`],
  ])("formats %d bytes in %s", (bytes, locale, expected) => {
    expect(formatBytes(bytes, locale)).toBe(expected);
  });

  it("rejects negative sizes", () => {
    expect(() => formatBytes(-1, "en")).toThrow(RangeError);
  });
});

describe("formatDuration", () => {
  it.each([
    [0, "0:00"],
    [7.9, "0:07"],
    [222, "3:42"],
    [3600, "1:00:00"],
    [3725, "1:02:05"],
  ])("formats %d seconds", (seconds, expected) => {
    expect(formatDuration(seconds)).toBe(expected);
  });
});
