/**
 * An audio file's loudness over time, as the worker stores it (docs/design.md §14): a 16-byte header ("DKWF",
 * version 1, three zero bytes, seconds per slice as float32, slice count as uint32, little-endian), then three bytes
 * per slice: the lowest sample's size, the highest sample and the RMS, each 0–255 with 255 the loudest sample in the
 * file. Shared by web and the app, which draw it the same way.
 */
export type Waveform = {
  secondsPerSlice: number;
  low: Uint8Array;
  high: Uint8Array;
  rms: Uint8Array;
};

/** Reads the stored format. Throws on anything else, so a broken file falls back to the plain timeline. */
export function parseWaveform(buffer: ArrayBuffer): Waveform {
  const view = new DataView(buffer);
  const magic = buffer.byteLength >= 16 ? String.fromCharCode(...new Uint8Array(buffer, 0, 4)) : "";
  if (magic !== "DKWF" || view.getUint8(4) !== 1) throw new Error("Not a waveform.");
  const secondsPerSlice = view.getFloat32(8, true);
  const count = view.getUint32(12, true);
  if (buffer.byteLength !== 16 + count * 3) throw new Error("The waveform is cut short.");
  const bytes = new Uint8Array(buffer, 16);
  const low = new Uint8Array(count);
  const high = new Uint8Array(count);
  const rms = new Uint8Array(count);
  for (let i = 0; i < count; i++) {
    low[i] = bytes[i * 3] ?? 0;
    high[i] = bytes[i * 3 + 1] ?? 0;
    rms[i] = bytes[i * 3 + 2] ?? 0;
  }
  return { secondsPerSlice, low, high, rms };
}

/** One column to draw: how far the waveform reaches up and down, and its loudness, as fractions of the height's half. */
export type WaveColumn = { high: number; low: number; rms: number };

/**
 * The waveform folded into `count` columns across the recording, keeping each column's extremes so short peaks still
 * show, as Audacity draws it zoomed out. `lengthSeconds` is the player's length, so the columns line up with the
 * timeline even if the decoder's count was a little off.
 */
export function waveColumns(waveform: Waveform, count: number, lengthSeconds?: number): WaveColumn[] {
  const slices = waveform.high.length;
  const total = lengthSeconds && lengthSeconds > 0 ? lengthSeconds / waveform.secondsPerSlice : slices;
  const columns: WaveColumn[] = [];
  for (let c = 0; c < count; c++) {
    const from = Math.floor((c * total) / count);
    const to = Math.max(from + 1, Math.floor(((c + 1) * total) / count));
    let high = 0;
    let low = 0;
    let rms = 0;
    for (let i = from; i < to && i < slices; i++) {
      high = Math.max(high, waveform.high[i] ?? 0);
      low = Math.max(low, waveform.low[i] ?? 0);
      rms = Math.max(rms, waveform.rms[i] ?? 0);
    }
    columns.push({ high: high / 255, low: low / 255, rms: rms / 255 });
  }
  return columns;
}

/**
 * SVG paths for the columns, in a box `count` wide from y = -1 (top) to 1: the peaks' outline and the RMS core,
 * each a closed shape around the middle line. Silence keeps a hairline, so the timeline never disappears.
 */
export function wavePaths(columns: readonly WaveColumn[]): { peaks: string; rms: string } {
  const min = 0.012;
  const y = (v: number) => +Math.max(min, v).toFixed(3);
  const top = (pick: (c: WaveColumn) => number) => columns.map((c, i) => `L${i} ${-y(pick(c))}L${i + 1} ${-y(pick(c))}`).join("");
  const bottom = (pick: (c: WaveColumn) => number) =>
    columns
      .map((c, i) => `L${i + 1} ${y(pick(c))}L${i} ${y(pick(c))}`)
      .reverse()
      .join("");
  const shape = (up: (c: WaveColumn) => number, down: (c: WaveColumn) => number) =>
    columns.length ? `M0 0${top(up)}L${columns.length} 0${bottom(down)}Z` : "";
  return { peaks: shape((c) => c.high, (c) => c.low), rms: shape((c) => c.rms, (c) => c.rms) };
}
