/**
 * Choosing pages for merging and splitting PDFs (docs/design.md §8.4). Shared by web and the app, so both name parts
 * and write page ranges the way the server reads them.
 */

/** A part of a split: the pages of the original that go in it, from 1, in order. */
export type PageList = number[];

/** "1-3, 5" for [1, 2, 3, 5], as the server's ranges read; runs only count when they go up by one. */
export function rangesText(pages: PageList, dash = "-"): string {
  const runs: string[] = [];
  let start = 0;
  for (let i = 1; i <= pages.length; i++) {
    if (i < pages.length && pages[i] === pages[i - 1]! + 1) continue;
    const first = pages[start]!;
    const last = pages[i - 1]!;
    runs.push(first === last ? `${first}` : `${first}${dash}${last}`);
    start = i;
  }
  return runs.join(", ");
}

export type PagesProblem =
  | { kind: "empty" }
  | { kind: "invalid"; text: string }
  | { kind: "backwards"; text: string }
  | { kind: "outside"; text: string; pageCount: number };

/** Reads "1-3, 5" into its pages, as the server does: en and em dashes count as hyphens, ranges may overlap. */
export function parsePages(text: string, pageCount: number): { pages: PageList } | { problem: PagesProblem } {
  const pages: PageList = [];
  const pieces = text
    .split(",")
    .map((piece) => piece.trim())
    .filter(Boolean);
  if (pieces.length === 0) return { problem: { kind: "empty" } };
  for (const piece of pieces) {
    const match = /^(\d+)\s*(?:[-–—]\s*(\d+))?$/.exec(piece);
    if (!match) return { problem: { kind: "invalid", text: piece } };
    const first = Number(match[1]);
    const last = match[2] === undefined ? first : Number(match[2]);
    if (last < first) return { problem: { kind: "backwards", text: piece } };
    if (first < 1 || last > pageCount) return { problem: { kind: "outside", text: piece, pageCount } };
    for (let page = first; page <= last; page++) pages.push(page);
  }
  return { pages };
}

/**
 * The parts a page strip makes: a new part starts after each page in `cuts`, and pages in `left` are left out.
 * Parts with no pages left in them are dropped.
 */
export function partsFromCuts(pageCount: number, cuts: ReadonlySet<number>, left: ReadonlySet<number> = new Set()): PageList[] {
  const parts: PageList[] = [];
  let part: PageList = [];
  for (let page = 1; page <= pageCount; page++) {
    if (!left.has(page)) part.push(page);
    if (cuts.has(page) || page === pageCount) {
      if (part.length > 0) parts.push(part);
      part = [];
    }
  }
  return parts;
}

/** Cuts after every `size` pages, for the "Every page" and "Every 2 pages" presets. */
export function everyCuts(pageCount: number, size: number): Set<number> {
  const cuts = new Set<number>();
  for (let page = size; page < pageCount; page += size) cuts.add(page);
  return cuts;
}

/** A file name without its extension: "Kvitto.2025.pdf" gives "Kvitto.2025". */
export function stemOf(name: string): string {
  const dot = name.lastIndexOf(".");
  return dot > 0 ? name.slice(0, dot) : name;
}

/** Puts ".pdf" on a name typed without it. */
export function withPdfExtension(name: string): string {
  const trimmed = name.trim();
  return /\.pdf$/i.test(trimmed) ? trimmed : `${trimmed}.pdf`;
}
