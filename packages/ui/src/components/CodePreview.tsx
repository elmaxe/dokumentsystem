import { foldGutter, foldKeymap, syntaxHighlighting } from "@codemirror/language";
import { openSearchPanel, search, searchKeymap } from "@codemirror/search";
import { Compartment, EditorState, RangeSetBuilder } from "@codemirror/state";
import { Decoration, EditorView, keymap, lineNumbers, highlightActiveLineGutter } from "@codemirror/view";
import { findHits } from "@dokument/core";
import { useEffect, useRef } from "react";
import { useTranslation } from "react-i18next";
import { codeHighlighter, languageForFile } from "../lib/code";

/** Colours and type from the tokens (§16.2), so the editor follows the light and dark themes. */
const theme = EditorView.theme({
  "&": {
    height: "100%",
    color: "var(--color-on-surface)",
    backgroundColor: "var(--color-surface-container-lowest)",
    fontSize: "var(--text-body-sm)",
  },
  "&.cm-focused": { outline: "none" },
  ".cm-scroller": { fontFamily: "var(--font-mono)", lineHeight: "1.6" },
  ".cm-content": { padding: "8px 0", caretColor: "var(--color-on-surface)" },
  ".cm-line": { padding: "0 16px 0 8px" },
  ".cm-gutters": {
    backgroundColor: "var(--color-surface-container-lowest)",
    color: "var(--color-on-surface-variant)",
    border: "none",
  },
  ".cm-lineNumbers .cm-gutterElement": { padding: "0 4px 0 16px", minWidth: "3ch" },
  ".cm-activeLineGutter": { backgroundColor: "transparent", color: "var(--color-on-surface)" },
  ".cm-foldGutter .cm-gutterElement": { cursor: "pointer", padding: "0 2px" },
  ".cm-foldPlaceholder": {
    backgroundColor: "var(--color-surface-container-high)",
    color: "var(--color-on-surface-variant)",
    border: "none",
    padding: "0 6px",
    borderRadius: "4px",
  },
  "&.cm-focused .cm-selectionBackground, .cm-selectionBackground, ::selection": {
    backgroundColor: "var(--color-primary-container)",
  },
  ".cm-searchMatch": { backgroundColor: "var(--color-warning-container)", borderRadius: "2px" },
  ".cm-searchMatch-selected": { outline: "2px solid var(--color-warning)" },
  ".cm-hit": { backgroundColor: "var(--color-primary-container)", color: "var(--color-on-primary-container)", borderRadius: "2px" },
  ".cm-panels": {
    backgroundColor: "var(--color-surface-container-low)",
    color: "var(--color-on-surface)",
    fontFamily: "var(--font-sans)",
  },
  ".cm-panels-top": { borderBottom: "1px solid var(--color-outline-variant)" },
  ".cm-panel.cm-search": { padding: "8px 40px 8px 12px", display: "flex", flexWrap: "wrap", alignItems: "center", gap: "8px" },
  ".cm-panel.cm-search br": { display: "none" },
  ".cm-panel.cm-search label": { display: "inline-flex", alignItems: "center", gap: "4px", fontSize: "var(--text-body-sm)" },
  ".cm-panel.cm-search [name=close]": { top: "8px", right: "8px", fontSize: "20px", color: "var(--color-on-surface-variant)" },
  ".cm-textfield": {
    height: "36px",
    padding: "0 12px",
    border: "1px solid var(--color-outline)",
    borderRadius: "8px",
    backgroundColor: "var(--color-surface-container-lowest)",
    color: "var(--color-on-surface)",
    fontSize: "var(--text-body-md)",
    margin: "0",
  },
  ".cm-button": {
    height: "32px",
    padding: "0 12px",
    border: "1px solid var(--color-outline)",
    borderRadius: "16px",
    backgroundImage: "none",
    backgroundColor: "transparent",
    color: "var(--color-primary)",
    fontSize: "var(--text-label-lg)",
    fontWeight: "600",
    textTransform: "none",
    margin: "0",
  },
});

/** Search hits from the search page, marked the way the viewer marks them elsewhere (§13.3). */
function hitMarks(text: string, terms: readonly string[]) {
  const builder = new RangeSetBuilder<Decoration>();
  const mark = Decoration.mark({ class: "cm-hit" });
  for (const [from, to] of findHits(text, terms)) builder.add(from, to, mark);
  return builder.finish();
}

export type CodePreviewProps = {
  text: string;
  /** For the language, from the extension or a name like `Dockerfile`. */
  fileName: string;
  wrap: boolean;
  terms?: readonly string[];
  /** Bump to open the find bar, e.g. from the viewer's top bar. */
  findRequest?: number;
};

/**
 * A text file in a read-only CodeMirror 6 editor (§14): line numbers, folding, syntax highlighting for the file's
 * language and find with Ctrl+F. Loaded only when a text file is opened.
 */
export default function CodePreview({ text, fileName, wrap, terms, findRequest }: CodePreviewProps) {
  const { t } = useTranslation();
  const host = useRef<HTMLDivElement>(null);
  const view = useRef<EditorView | null>(null);
  const wrapping = useRef(new Compartment());
  const language = useRef(new Compartment());

  useEffect(() => {
    if (!host.current) return;
    const hits = terms?.length ? hitMarks(text, terms) : null;
    const editor = new EditorView({
      parent: host.current,
      state: EditorState.create({
        doc: text,
        extensions: [
          EditorState.readOnly.of(true),
          EditorState.phrases.of({
            Find: t("code.find"),
            next: t("code.next"),
            previous: t("code.previous"),
            all: t("code.all"),
            "match case": t("code.matchCase"),
            regexp: t("code.regexp"),
            "by word": t("code.byWord"),
            close: t("code.close"),
            "Fold line": t("code.fold"),
            "Unfold line": t("code.unfold"),
            "folded code": t("code.folded"),
            "Go to line": t("code.goToLine"),
            go: t("code.go"),
            "current match": t("code.currentMatch"),
            "on line": t("code.onLine"),
          }),
          lineNumbers(),
          highlightActiveLineGutter(),
          foldGutter(),
          syntaxHighlighting(codeHighlighter),
          search({ top: true }),
          keymap.of([...searchKeymap, ...foldKeymap]),
          wrapping.current.of([]),
          language.current.of([]),
          hits ? EditorView.decorations.of(hits) : [],
          EditorView.contentAttributes.of({ "aria-label": fileName, "aria-readonly": "true" }),
          theme,
        ],
      }),
    });
    view.current = editor;
    const first = hits?.iter();
    if (first?.value) editor.dispatch({ effects: EditorView.scrollIntoView(first.from, { y: "center" }) });

    let cancelled = false;
    languageForFile(fileName)
      ?.load()
      .then((support) => {
        if (!cancelled) editor.dispatch({ effects: language.current.reconfigure(support) });
      })
      .catch(() => {});
    return () => {
      cancelled = true;
      editor.destroy();
      view.current = null;
    };
  }, [text, fileName, terms, t]);

  // After the editor above, so a new editor gets the setting too.
  useEffect(() => {
    view.current?.dispatch({ effects: wrapping.current.reconfigure(wrap ? EditorView.lineWrapping : []) });
  }, [wrap, text, fileName, terms, t]);

  useEffect(() => {
    if (findRequest && view.current) openSearchPanel(view.current);
  }, [findRequest]);

  return <div ref={host} className="h-full min-h-0" />;
}
