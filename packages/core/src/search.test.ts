import { describe, expect, it } from "vitest";
import { findHits, matchesTerm, splitHighlights } from "./search";

describe("matchesTerm", () => {
  it.each([
    ["Kvittot", "kvitto", true],
    ["kvitton", "kvitto", true],
    ["receipts", "receipt", true],
    ["kvittoarkivet", "kvitto", false],
    ["hyresavtal", "hyresavtl", true],
    ["ikea", "ika", false],
    ["katt", "kat", false],
  ])("%s for %s is %s, like the server", (word, term, expected) => {
    expect(matchesTerm(word, term)).toBe(expected);
  });
});

describe("findHits", () => {
  it("marks whole words, with å, ä and ö", () => {
    const text = "Kvitto IKEA, Kvittot för Söderhamn";
    expect(findHits(text, ["kvitto", "söderhamn"]).map(([s, e]) => text.slice(s, e))).toEqual([
      "Kvitto",
      "Kvittot",
      "Söderhamn",
    ]);
  });
});

describe("splitHighlights", () => {
  it("splits around ranges and ignores bad ones", () => {
    expect(splitHighlights("a IKEA b", [[2, 6], [3, 4], [9, 12]])).toEqual([
      { text: "a ", hit: false },
      { text: "IKEA", hit: true },
      { text: " b", hit: false },
    ]);
  });
});
