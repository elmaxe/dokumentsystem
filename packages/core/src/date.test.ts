import { describe, expect, it } from "vitest";
import { formatFileDate } from "./date";

const now = new Date(2026, 9, 22, 15, 0);

describe("formatFileDate", () => {
  it.each([
    [new Date(2026, 9, 22, 9, 14), "en", "Today 09:14"],
    [new Date(2026, 9, 22, 9, 14), "sv", "I dag 09:14"],
    [new Date(2026, 9, 21, 23, 0), "en", "Yesterday"],
    [new Date(2026, 2, 12), "en", "12 Mar"],
    [new Date(2025, 5, 21), "en", "21 Jun 2025"],
    [new Date(2025, 5, 21), "sv", "21 juni 2025"],
  ])("formats %s in %s", (date, locale, expected) => {
    expect(formatFileDate(date, locale, { now })).toBe(expected);
  });

  it("keeps relative days lower case inside a sentence", () => {
    expect(formatFileDate(new Date(2026, 9, 22, 9, 14), "en", { now, inSentence: true })).toBe("today 09:14");
  });
});
