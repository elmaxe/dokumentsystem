import { describe, expect, it } from "vitest";
import { seekFrame } from "./media";

const sheet = { intervalMs: 2000, count: 10, columns: 4, frameWidth: 160, frameHeight: 90 };

describe("seekFrame", () => {
  it("scales the frame to fit the box and finds it in the sheet", () => {
    expect(seekFrame(sheet, 13_000, 144, 128)).toEqual({
      index: 6,
      width: 144,
      height: 81,
      sheetWidth: 576,
      sheetHeight: 243,
      x: -288,
      y: -81,
    });
  });

  it("keeps to the first and last frames", () => {
    expect(seekFrame(sheet, -500, 160, 90).index).toBe(0);
    expect(seekFrame(sheet, 999_000, 160, 90).index).toBe(9);
  });
});
