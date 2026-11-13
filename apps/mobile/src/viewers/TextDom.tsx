"use dom";

import { findHits } from "@dokument/core";
import type { DOMProps } from "expo/dom";
import { useEffect, useRef, type CSSProperties } from "react";
import Markdown, { type Components } from "react-markdown";
import remarkGfm from "remark-gfm";

export type TextColors = {
  background: string;
  text: string;
  muted: string;
  link: string;
  code: string;
  border: string;
  /** Search hits: warning-container. */
  mark: string;
};

/**
 * Text and Markdown in a web view (docs/design.md §15.3), so Markdown renders as on the web: GitHub's flavour, no raw
 * HTML, and no images from the web, which would tell their servers the file was opened. Links open in the browser.
 */
export default function TextDom({
  text,
  markdown,
  colors,
  imageLabel,
  openLink,
  terms,
  onMarks,
}: {
  text: string;
  markdown: boolean;
  colors: TextColors;
  /** "Image: {{alt}}", shown where a Markdown image would be. */
  imageLabel: string;
  openLink: (url: string) => Promise<void>;
  /** Opened from search: the words to mark, by the same rule as the server (docs/design.md §13.3). */
  terms?: string[];
  /** How many were marked. */
  onMarks?: (count: number) => Promise<void>;
  dom?: DOMProps;
}) {
  const root = useRef<HTMLDivElement>(null);
  const termsKey = terms?.join(" ") ?? "";
  const counted = useRef(onMarks);
  useEffect(() => {
    counted.current = onMarks;
  }, [onMarks]);

  // Marks the hits in what's drawn, a text node at a time, and scrolls to the first. The content is drawn afresh
  // whenever the terms change (its key), so old marks never stay behind.
  useEffect(() => {
    const element = root.current;
    if (!element || !termsKey) return;
    const words = termsKey.split(" ");
    const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
    const nodes: Text[] = [];
    for (let node = walker.nextNode(); node; node = walker.nextNode()) nodes.push(node as Text);
    let count = 0;
    for (const node of nodes) {
      const value = node.nodeValue ?? "";
      const hits = findHits(value, words);
      if (hits.length === 0) continue;
      const parts = document.createDocumentFragment();
      let at = 0;
      for (const [start, end] of hits) {
        parts.append(value.slice(at, start));
        const mark = document.createElement("mark");
        mark.textContent = value.slice(start, end);
        mark.style.background = colors.mark;
        mark.style.color = "inherit";
        mark.style.borderRadius = "2px";
        parts.append(mark);
        at = end;
        count++;
      }
      parts.append(value.slice(at));
      node.replaceWith(parts);
    }
    element.querySelector("mark")?.scrollIntoView({ block: "center" });
    void counted.current?.(count);
  }, [termsKey, colors.mark]);

  const page: CSSProperties = {
    margin: 0,
    padding: "16px 16px 48px",
    background: colors.background,
    color: colors.text,
    fontFamily: "system-ui, sans-serif",
    fontSize: 16,
    lineHeight: 1.6,
    overflowWrap: "anywhere",
    minHeight: "100vh",
    boxSizing: "border-box",
  };
  const code: CSSProperties = {
    fontFamily: "ui-monospace, Menlo, monospace",
    fontSize: 14,
    background: colors.code,
    borderRadius: 4,
    padding: "1px 4px",
  };

  if (!markdown) {
    return (
      <div ref={root} key={termsKey}>
      <pre
        style={{
          ...page,
          fontFamily: "ui-monospace, Menlo, monospace",
          fontSize: 14,
          whiteSpace: "pre-wrap",
        }}
      >
        {text}
      </pre>
      </div>
    );
  }

  // Spelled out, so the page looks the same whatever the web view's own stylesheet does.
  const heading = (size: number): CSSProperties => ({
    fontSize: size,
    lineHeight: 1.3,
    margin: "24px 0 12px",
  });
  const components: Components = {
    h1: ({ children }) => <h1 style={heading(28)}>{children}</h1>,
    h2: ({ children }) => <h2 style={heading(22)}>{children}</h2>,
    h3: ({ children }) => <h3 style={heading(18)}>{children}</h3>,
    p: ({ children }) => <p style={{ margin: "0 0 16px" }}>{children}</p>,
    ul: ({ children, className }) => (
      <ul
        style={{
          margin: "0 0 16px",
          paddingLeft: className ? 4 : 24,
          listStyle: className ? "none" : "disc",
        }}
      >
        {children}
      </ul>
    ),
    ol: ({ children }) => (
      <ol style={{ margin: "0 0 16px", paddingLeft: 24, listStyle: "decimal" }}>{children}</ol>
    ),
    a: ({ href, children }) => (
      <a
        href={href}
        style={{ color: colors.link }}
        onClick={(event) => {
          event.preventDefault();
          if (href && /^https?:|^mailto:/i.test(href)) void openLink(href);
        }}
      >
        {children}
      </a>
    ),
    img: ({ alt }) => (
      <span style={{ color: colors.muted }}>[{imageLabel.replace("{{alt}}", alt ?? "")}]</span>
    ),
    code: ({ children, className }) => (
      <code style={className ? undefined : code} className={className}>
        {children}
      </code>
    ),
    pre: ({ children }) => (
      <pre style={{ ...code, display: "block", padding: 12, overflowX: "auto", whiteSpace: "pre" }}>
        {children}
      </pre>
    ),
    blockquote: ({ children }) => (
      <blockquote
        style={{
          margin: "0 0 16px",
          padding: "0 12px",
          borderLeft: `4px solid ${colors.border}`,
          color: colors.muted,
        }}
      >
        {children}
      </blockquote>
    ),
    table: ({ children }) => (
      <div style={{ overflowX: "auto", marginBottom: 16 }}>
        <table style={{ borderCollapse: "collapse" }}>{children}</table>
      </div>
    ),
    th: ({ children }) => (
      <th style={{ border: `1px solid ${colors.border}`, padding: "4px 8px" }}>{children}</th>
    ),
    td: ({ children }) => (
      <td style={{ border: `1px solid ${colors.border}`, padding: "4px 8px" }}>{children}</td>
    ),
    hr: () => <hr style={{ border: 0, borderTop: `1px solid ${colors.border}` }} />,
  };

  return (
    <div ref={root} key={termsKey} style={page}>
      <Markdown remarkPlugins={[remarkGfm]} components={components}>
        {text}
      </Markdown>
    </div>
  );
}
