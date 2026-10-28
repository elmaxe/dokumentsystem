import { findHits, splitHighlights } from "@dokument/core";
import { useEffect, useState, type ComponentProps, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import Markdown, { type Components } from "react-markdown";
import remarkGfm from "remark-gfm";
import { highlight, languageByName, type CodeSpan } from "../lib/code";
import { Button } from "./Button";
import { cn } from "../lib/cn";
import { Icon } from "./Icon";

type MdNode = { type: string; value?: string; children?: MdNode[]; data?: Record<string, unknown> };

/** A remark plugin that wraps search hits in `<mark>`, in text only, so links and code keep working (§13.3). */
const remarkMarks = (terms: readonly string[]) => () => (tree: MdNode) => {
  const visit = (node: MdNode) => {
    if (!node.children) return;
    node.children = node.children.flatMap((child): MdNode[] => {
      if (child.type !== "text" || !child.value) {
        visit(child);
        return [child];
      }
      const hits = findHits(child.value, terms);
      if (hits.length === 0) return [child];
      return splitHighlights(child.value, hits).map((part) =>
        part.hit
          ? { type: "mark", data: { hName: "mark" }, children: [{ type: "text", value: part.text }] }
          : { type: "text", value: part.text },
      );
    });
  };
  visit(tree);
};

const webUrl = /^https?:\/\//i;
const linkUrl = /^(https?:|mailto:)/i;

function CodeBlock({ code, lang }: { code: string; lang?: string }) {
  const [spans, setSpans] = useState<CodeSpan[] | null>(null);
  useEffect(() => {
    const language = lang ? languageByName(lang) : null;
    if (!language) return;
    let cancelled = false;
    highlight(code, language)
      .then((result) => !cancelled && setSpans(result))
      .catch(() => {});
    return () => {
      cancelled = true;
    };
  }, [code, lang]);
  return (
    <code className="font-mono">
      {spans ? spans.map((span, i) => (span.className ? <span key={i} className={span.className}>{span.text}</span> : span.text)) : code}
    </code>
  );
}

function BlockedImage({ src, alt, onAllow }: { src: string; alt?: string; onAllow: () => void }) {
  const { t } = useTranslation();
  let host = src;
  try {
    host = new URL(src).host;
  } catch {
    // Shown as written.
  }
  return (
    <span className="my-2 flex flex-wrap items-center gap-3 rounded-md border border-dashed border-outline bg-surface-container-low px-4 py-3 text-body-md text-on-surface-variant not-italic">
      <Icon name="image_not_supported" />
      <span className="min-w-48 flex-1">
        {alt ? <span className="block text-on-surface">{alt}</span> : null}
        {t("markdown.imageBlocked", { host })}
      </span>
      <Button variant="outlined" onClick={onAllow}>
        {t("markdown.loadImages")}
      </Button>
    </span>
  );
}

const heading = "mt-6 mb-3 font-medium first:mt-0";

/**
 * A Markdown file, formatted (§14): GitHub-flavoured, with raw HTML left as text so nothing in a file runs in our page.
 * Images from the web load only when asked, so a remote server can't tell the file was opened. Images and links to
 * other files in the drive aren't resolved yet; they show as text.
 */
export default function MarkdownPreview({ text, terms }: { text: string; terms?: readonly string[] }) {
  const { t } = useTranslation();
  const [imagesAllowed, setImagesAllowed] = useState(false);

  const components: Components = {
    h1: (props) => <h1 className={`${heading} text-headline-md`}>{props.children}</h1>,
    h2: (props) => <h2 className={`${heading} border-b border-outline-variant pb-1 text-headline-sm`}>{props.children}</h2>,
    h3: (props) => <h3 className={`${heading} text-title-lg`}>{props.children}</h3>,
    h4: (props) => <h4 className={`${heading} text-title-md`}>{props.children}</h4>,
    h5: (props) => <h5 className={`${heading} text-title-sm`}>{props.children}</h5>,
    h6: (props) => <h6 className={`${heading} text-title-sm text-on-surface-variant`}>{props.children}</h6>,
    p: (props) => <p className="my-3">{props.children}</p>,
    ul: (props) => <ul className={`my-3 pl-6 ${props.className?.includes("contains-task-list") ? "list-none pl-1" : "list-disc"}`}>{props.children}</ul>,
    ol: (props) => <ol className="my-3 list-decimal pl-6">{props.children}</ol>,
    li: (props) => <li className="my-1">{props.children}</li>,
    // Task list boxes, drawn rather than as inputs: the file is only being read.
    input: (props: ComponentProps<"input">) => (
      <Icon
        name={props.checked ? "check_box" : "check_box_outline_blank"}
        filled={props.checked}
        size="sm"
        label={props.checked ? t("markdown.done") : t("markdown.notDone")}
        className={cn("mr-2 align-[-4px]", props.checked ? "text-primary" : "text-on-surface-variant")}
      />
    ),
    blockquote: (props) => (
      <blockquote className="my-3 border-l-4 border-outline-variant pl-4 text-on-surface-variant">{props.children}</blockquote>
    ),
    hr: () => <hr className="my-6 border-0 border-t border-outline-variant" />,
    a: ({ href, children }) =>
      href && linkUrl.test(href) ? (
        <a href={href} target="_blank" rel="noopener noreferrer" className="text-primary underline">
          {children}
        </a>
      ) : (
        <span className="text-primary" title={href}>
          {children}
        </span>
      ),
    img: ({ src, alt }) => {
      const url = typeof src === "string" ? src : "";
      if (!webUrl.test(url)) return <span className="text-on-surface-variant italic">{alt || url}</span>;
      if (!imagesAllowed) return <BlockedImage src={url} alt={alt} onAllow={() => setImagesAllowed(true)} />;
      return <img src={url} alt={alt ?? ""} referrerPolicy="no-referrer" loading="lazy" className="inline max-w-full rounded-sm" />;
    },
    table: (props) => (
      <div className="my-3 overflow-x-auto">
        <table className="border-collapse text-body-md">{props.children}</table>
      </div>
    ),
    th: (props) => (
      <th className="border border-outline-variant bg-surface-container-low px-3 py-1.5 text-left font-semibold" style={{ textAlign: props.style?.textAlign }}>
        {props.children}
      </th>
    ),
    td: (props) => (
      <td className="border border-outline-variant px-3 py-1.5" style={{ textAlign: props.style?.textAlign }}>
        {props.children}
      </td>
    ),
    pre: (props) => (
      <pre className="my-3 overflow-x-auto rounded-md bg-surface-container-low p-4 text-body-sm leading-relaxed">{props.children}</pre>
    ),
    code: ({ className, children }) => {
      const lang = /language-([\w+#-]+)/.exec(className ?? "")?.[1];
      const code = String(children ?? "");
      // Fenced blocks end in a newline; inline code never does.
      if (lang || code.endsWith("\n")) return <CodeBlock code={code.replace(/\n$/, "")} lang={lang} />;
      return <code className="rounded-xs bg-surface-container-high px-1 py-0.5 font-mono text-[0.9em]">{children as ReactNode}</code>;
    },
  };

  return (
    <article
      ref={(element) => element?.querySelector("mark")?.scrollIntoView({ block: "center" })}
      aria-label={t("markdown.formatted")}
      className="text-body-lg break-words"
    >
      <Markdown remarkPlugins={terms?.length ? [remarkGfm, remarkMarks(terms)] : [remarkGfm]} components={components}>
        {text}
      </Markdown>
    </article>
  );
}
