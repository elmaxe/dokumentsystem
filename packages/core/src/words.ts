/** A word on a page and its box, as fractions of the page's size: recognized by the server, or a PDF's own text. */
export type PageWord = { x: number; y: number; width: number; height: number; text: string };

/**
 * Words in reading order as text, for copying from the viewer (docs/design.md §14): a space between words on a line
 * and a line break where the next word starts lower down or back to the left. `text` is the selected part of a word,
 * when a selection starts or ends inside one.
 */
export function wordsToText(picked: readonly { word: PageWord; text?: string }[]): string {
  let text = "";
  let previous: PageWord | undefined;
  for (const { word, text: shown = word.text } of picked) {
    if (previous) text += word.y > previous.y + previous.height / 2 || word.x < previous.x ? "\n" : " ";
    text += shown;
    previous = word;
  }
  return text;
}
