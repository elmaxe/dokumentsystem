/** The longest ending that still counts as the same word. */
const maxEnding = 4;

/**
 * Whether a word in a file counts as a hit for a search term, by the same rule the server uses for snippets
 * (docs/design.md §13.3). The index matches Swedish and English stems, so a word is a hit when it is the term, when one
 * is the other plus a short ending ("kvitto" and "kvittot"), or, for longer words, when it is one typo away.
 */
export function matchesTerm(word: string, term: string): boolean {
  word = word.toLowerCase();
  if (word === term) return true;
  const [shorter, longer] = word.length <= term.length ? [word, term] : [term, word];
  if (shorter.length >= 4 && longer.length - shorter.length <= maxEnding && longer.startsWith(shorter)) return true;
  return shorter.length >= 5 && longer.length - shorter.length <= 1 && oneEditApart(word, term);
}

const wordPattern = /[\p{L}\p{N}]+/gu;

/** The hits in a text, as `[start, end)` offsets, for marking. Words are split the way the index splits them. */
export function findHits(text: string, terms: readonly string[]): [number, number][] {
  if (terms.length === 0) return [];
  const hits: [number, number][] = [];
  for (const match of text.matchAll(wordPattern)) {
    if (terms.some((term) => matchesTerm(match[0], term))) hits.push([match.index, match.index + match[0].length]);
  }
  return hits;
}

/** Splits text into plain and marked parts, from `[start, end)` ranges, for rendering with <mark>. */
export function splitHighlights(text: string, ranges: readonly (readonly number[])[]): { text: string; hit: boolean }[] {
  const parts: { text: string; hit: boolean }[] = [];
  let at = 0;
  for (const [start = 0, end = 0] of [...ranges].sort((a, b) => (a[0] ?? 0) - (b[0] ?? 0))) {
    if (start < at || end <= start || end > text.length) continue;
    if (start > at) parts.push({ text: text.slice(at, start), hit: false });
    parts.push({ text: text.slice(start, end), hit: true });
    at = end;
  }
  if (at < text.length) parts.push({ text: text.slice(at), hit: false });
  return parts;
}

function oneEditApart(a: string, b: string): boolean {
  a = a.toLowerCase();
  b = b.toLowerCase();
  if (Math.abs(a.length - b.length) > 1) return false;
  let i = 0;
  let j = 0;
  let edits = 0;
  while (i < a.length && j < b.length) {
    if (a[i] === b[j]) {
      i++;
      j++;
      continue;
    }
    if (++edits > 1) return false;
    if (a.length > b.length) i++;
    else if (b.length > a.length) j++;
    else {
      i++;
      j++;
    }
  }
  return edits + (a.length - i) + (b.length - j) <= 1;
}
