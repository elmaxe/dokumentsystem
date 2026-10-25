import { LanguageDescription } from "@codemirror/language";
import { languages } from "@codemirror/language-data";
import { highlightCode, tagHighlighter, tags as t } from "@lezer/highlight";

/**
 * Syntax colours for the text viewer and Markdown code blocks (docs/design.md §14), as token utilities so light and
 * dark follow the theme (§16.2). A tag takes the rule naming it most exactly, and a definition
 * doesn't fall back to its plain form, so function definitions are listed on their own.
 */
export const codeHighlighter = tagHighlighter([
  { tag: [t.keyword, t.operatorKeyword, t.controlKeyword, t.definitionKeyword, t.moduleKeyword, t.self], class: "text-code-keyword" },
  { tag: [t.string, t.special(t.string), t.regexp, t.escape, t.character, t.inserted], class: "text-code-string" },
  { tag: [t.number, t.bool, t.null, t.atom, t.literal, t.deleted], class: "text-code-number" },
  { tag: [t.comment, t.meta, t.processingInstruction], class: "text-code-comment italic" },
  {
    tag: [
      t.function(t.variableName),
      t.function(t.definition(t.variableName)),
      t.function(t.propertyName),
      t.function(t.definition(t.propertyName)),
      t.macroName,
      t.labelName,
    ],
    class: "text-code-function",
  },
  { tag: [t.typeName, t.className, t.namespace, t.tagName, t.attributeName, t.annotation], class: "text-code-type" },
  { tag: t.heading, class: "text-code-function font-semibold" },
  { tag: t.emphasis, class: "italic" },
  { tag: t.strong, class: "font-semibold" },
  { tag: t.strikethrough, class: "line-through" },
  { tag: [t.link, t.url], class: "text-primary" },
  { tag: t.invalid, class: "text-error" },
]);

/** The language for a file name, such as `sync.py` or `Dockerfile`; null for plain text. */
export const languageForFile = (fileName: string) => LanguageDescription.matchFilename(languages, fileName);

/** The language a Markdown fence names, such as `ts` or `yaml`. */
export const languageByName = (name: string) => LanguageDescription.matchLanguageName(languages, name, true);

export type CodeSpan = { text: string; className: string };

/** Highlights code outside an editor, for Markdown code blocks. Lines are split at "\n" by the caller. */
export async function highlight(code: string, language: LanguageDescription): Promise<CodeSpan[]> {
  const support = await language.load();
  const spans: CodeSpan[] = [];
  highlightCode(
    code,
    support.language.parser.parse(code),
    codeHighlighter,
    (text, className) => spans.push({ text, className }),
    () => spans.push({ text: "\n", className: "" }),
  );
  return spans;
}
