"use dom";

import type { DOMProps } from "expo/dom";
import type { CSSProperties } from "react";
import Markdown, { type Components } from "react-markdown";
import remarkGfm from "remark-gfm";

export type TextColors = {
  background: string;
  text: string;
  muted: string;
  link: string;
  code: string;
  border: string;
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
}: {
  text: string;
  markdown: boolean;
  colors: TextColors;
  /** "Image: {{alt}}", shown where a Markdown image would be. */
  imageLabel: string;
  openLink: (url: string) => Promise<void>;
  dom?: DOMProps;
}) {
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
    <div style={page}>
      <Markdown remarkPlugins={[remarkGfm]} components={components}>
        {text}
      </Markdown>
    </div>
  );
}
