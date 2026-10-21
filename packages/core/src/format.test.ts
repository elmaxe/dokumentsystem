import { describe, expect, it } from "vitest";
import { formatBytes } from "./format";

const nbsp = "\u00a0";

describe("formatBytes", () => {
  it.each([
    [0, "en", `0${nbsp}B`],
    [999, "en", `999${nbsp}B`],
    [1_400_000, "en", `1.4${nbsp}MB`],
    [1_400_000, "sv", `1,4${nbsp}MB`],
    [52_000_000, "sv", `52${nbsp}MB`],
    [3_200_000_000_000, "en", `3.2${nbsp}TB`],
  ])("formats %d bytes in %s", (bytes, locale, expected) => {
    expect(formatBytes(bytes, locale)).toBe(expected);
  });

  it("rejects negative sizes", () => {
    expect(() => formatBytes(-1, "en")).toThrow(RangeError);
  });
});
