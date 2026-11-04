import { describe, expect, it } from "vitest";
import { parseWaveform, waveColumns, wavePaths } from "./waveform";

function encode(secondsPerSlice: number, slices: [number, number, number][]): ArrayBuffer {
  const buffer = new ArrayBuffer(16 + slices.length * 3);
  const view = new DataView(buffer);
  "DKWF".split("").forEach((c, i) => view.setUint8(i, c.charCodeAt(0)));
  view.setUint8(4, 1);
  view.setFloat32(8, secondsPerSlice, true);
  view.setUint32(12, slices.length, true);
  slices.forEach(([low, high, rms], i) => {
    view.setUint8(16 + i * 3, low);
    view.setUint8(17 + i * 3, high);
    view.setUint8(18 + i * 3, rms);
  });
  return buffer;
}

describe("waveform", () => {
  it("reads the worker's format", () => {
    const wave = parseWaveform(encode(0.02, [[10, 20, 5], [255, 200, 100]]));
    expect(wave.secondsPerSlice).toBeCloseTo(0.02);
    expect([...wave.low]).toEqual([10, 255]);
    expect([...wave.high]).toEqual([20, 200]);
    expect([...wave.rms]).toEqual([5, 100]);
  });

  it("refuses anything else", () => {
    expect(() => parseWaveform(new ArrayBuffer(20))).toThrow();
    expect(() => parseWaveform(encode(0.02, [[1, 2, 3]]).slice(0, 18))).toThrow();
  });

  it("keeps each column's peaks when folding many slices into few", () => {
    const wave = parseWaveform(encode(1, [[0, 0, 0], [0, 255, 10], [51, 0, 0], [0, 0, 0]]));
    expect(waveColumns(wave, 2)).toEqual([
      { high: 1, low: 0, rms: 10 / 255 },
      { high: 0, low: 0.2, rms: 0 },
    ]);
  });

  it("lines the columns up with the player's length", () => {
    const wave = parseWaveform(encode(1, [[0, 255, 0], [0, 255, 0]]));
    // Four seconds long, but only two were decoded: the rest is silent, not stretched.
    expect(waveColumns(wave, 4, 4).map((c) => c.high)).toEqual([1, 1, 0, 0]);
  });

  it("draws closed shapes around the middle", () => {
    const { peaks, rms } = wavePaths([{ high: 0.5, low: 0.25, rms: 0.1 }]);
    expect(peaks).toBe("M0 0L0 -0.5L1 -0.5L1 0L1 0.25L0 0.25Z");
    expect(rms).toMatch(/^M0 0.*Z$/);
    expect(wavePaths([]).peaks).toBe("");
  });
});
