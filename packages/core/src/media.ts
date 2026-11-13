/**
 * A video's seek previews (docs/design.md §14): one image of `count` frames, `columns` to a row, left to right and top
 * to bottom. Frame `i` stands for the time from `i × intervalMs` up to the next one.
 */
export type SeekSheet = {
  intervalMs: number;
  count: number;
  columns: number;
  frameWidth: number;
  frameHeight: number;
};

/**
 * The frame for `ms`, scaled to fit a `boxWidth` × `boxHeight` box: its size, the whole sheet's size at that scale, and
 * how far to shift the sheet so the frame shows. Shared by web and the app, which cut the frame the same way.
 */
export function seekFrame(sheet: SeekSheet, ms: number, boxWidth: number, boxHeight: number) {
  const index = Math.min(sheet.count - 1, Math.max(0, Math.floor(ms / sheet.intervalMs)));
  const scale = Math.min(boxWidth / sheet.frameWidth, boxHeight / sheet.frameHeight);
  const width = Math.round(sheet.frameWidth * scale);
  const height = Math.round(sheet.frameHeight * scale);
  const rows = Math.ceil(sheet.count / sheet.columns);
  return {
    index,
    width,
    height,
    sheetWidth: sheet.columns * width,
    sheetHeight: rows * height,
    x: -(index % sheet.columns) * width,
    y: -Math.floor(index / sheet.columns) * height,
  };
}
