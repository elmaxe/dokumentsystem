import { describe, expect, it } from "vitest";
import { wordsToText, type PageWord } from "./words";

const word = (text: string, x: number, y: number): PageWord => ({ x, y, width: 0.1, height: 0.03, text });

describe("wordsToText", () => {
  it("puts spaces between words on a line and breaks between lines", () => {
    const words = [word("Mjölk", 0.1, 0.2), word("15,90", 0.7, 0.201), word("Kaffe", 0.1, 0.24), word("64,90", 0.7, 0.24)];
    expect(wordsToText(words.map((w) => ({ word: w })))).toBe("Mjölk 15,90\nKaffe 64,90");
  });

  it("starts a new line when the next word is back to the left, even on a slanted photo", () => {
    const words = [word("Tack", 0.3, 0.8), word("för", 0.42, 0.79), word("Totalt", 0.1, 0.795)];
    expect(wordsToText(words.map((w) => ({ word: w })))).toBe("Tack för\nTotalt");
  });

  it("uses the selected part of a word where the selection starts or ends inside it", () => {
    const words = [word("Totalt", 0.1, 0.5), word("130,19", 0.7, 0.5)];
    expect(wordsToText([{ word: words[0]!, text: "talt" }, { word: words[1]!, text: "130" }])).toBe("talt 130");
  });
});
