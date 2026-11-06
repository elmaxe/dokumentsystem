import { describe, expect, it } from "vitest";
import { everyCuts, parsePages, partsFromCuts, rangesText, stemOf, withPdfExtension } from "./pdf";

describe("rangesText", () => {
  it("joins runs that go up by one", () => {
    expect(rangesText([1, 2, 3, 5, 7, 8])).toBe("1-3, 5, 7-8");
    expect(rangesText([4])).toBe("4");
    expect(rangesText([3, 2, 1])).toBe("3, 2, 1");
    expect(rangesText([1, 2], "–")).toBe("1–2");
  });
});

describe("parsePages", () => {
  it("reads pages and ranges", () => {
    expect(parsePages("1-3, 5", 9)).toEqual({ pages: [1, 2, 3, 5] });
    expect(parsePages(" 2 – 4 ", 9)).toEqual({ pages: [2, 3, 4] });
  });

  it("says what's wrong", () => {
    expect(parsePages(" , ", 9)).toEqual({ problem: { kind: "empty" } });
    expect(parsePages("1-x", 9)).toEqual({ problem: { kind: "invalid", text: "1-x" } });
    expect(parsePages("5-2", 9)).toEqual({ problem: { kind: "backwards", text: "5-2" } });
    expect(parsePages("8-10", 9)).toEqual({ problem: { kind: "outside", text: "8-10", pageCount: 9 } });
  });
});

describe("partsFromCuts", () => {
  it("starts a part after each cut and leaves pages out", () => {
    expect(partsFromCuts(6, new Set([2, 4]))).toEqual([
      [1, 2],
      [3, 4],
      [5, 6],
    ]);
    expect(partsFromCuts(6, new Set([2]), new Set([1, 2, 5]))).toEqual([[3, 4, 6]]);
    expect(partsFromCuts(5, everyCuts(5, 2))).toEqual([[1, 2], [3, 4], [5]]);
  });
});

describe("names", () => {
  it("drops and adds the extension", () => {
    expect(stemOf("Kvitto.2025.pdf")).toBe("Kvitto.2025");
    expect(stemOf(".hidden")).toBe(".hidden");
    expect(withPdfExtension(" Avtal ")).toBe("Avtal.pdf");
    expect(withPdfExtension("Avtal.PDF")).toBe("Avtal.PDF");
  });
});
