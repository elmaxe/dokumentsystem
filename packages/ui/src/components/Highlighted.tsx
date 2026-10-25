import { findHits, splitHighlights } from "@dokument/core";
import { Fragment } from "react";

export type HighlightedProps = {
  text: string;
  /** Ranges to mark, as `[start, end)` offsets, as the server sends them for snippets. */
  ranges?: readonly (readonly number[])[];
  /** Or the search terms, matched with the server's rule (docs/design.md §13.3). */
  terms?: readonly string[];
};

/** Text with search hits marked. Text is never parsed as markup, so file content can't inject any. */
export function Highlighted({ text, ranges, terms }: HighlightedProps) {
  const parts = splitHighlights(text, ranges ?? findHits(text, terms ?? []));
  return (
    <>
      {parts.map((part, i) => (
        <Fragment key={i}>{part.hit ? <mark>{part.text}</mark> : part.text}</Fragment>
      ))}
    </>
  );
}
