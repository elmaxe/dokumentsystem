import { FileTypeIcon, useFormat } from "@dokument/ui";
import { useQuery } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { originQuery } from "./pdf";

const span = (first: number, last: number) => (first === last ? `${first}` : `${first}–${last}`);

/**
 * "Made from" in a file's info, for a file made by merging or splitting PDFs: which files and pages its current version
 * came from, with a link to each that's still there.
 */
export function MadeFrom({ id, versionId }: { id: string; versionId: string | null }) {
  const { t } = useTranslation();
  const format = useFormat();
  const navigate = useNavigate();
  const origin = useQuery(originQuery(id, versionId));
  if (!origin.data) return null;
  const { kind, at, by, lines } = origin.data;
  return (
    <section className="flex flex-col gap-2 border-t border-outline-variant pt-4">
      <h3 className="m-0 text-title-sm">{t("pdf.origin.title")}</h3>
      <p className="m-0 text-body-sm text-on-surface-variant">
        {t(kind === "merge" ? "pdf.origin.merged" : "pdf.origin.split", { name: by, date: format.date(at, { inSentence: true }) })}
      </p>
      <ul className="m-0 flex list-none flex-col p-0">
        {lines.map((line) => {
          const pages = t("pdf.origin.pages", { pages: span(line.first, line.last), count: line.last - line.first + 1 });
          const from = t("pdf.origin.fromPages", {
            pages: span(line.sourceFirst, line.sourceLast),
            count: line.sourceLast - line.sourceFirst + 1,
          });
          const state = line.state === "trashed" ? t("pdf.origin.trashed") : line.state === "gone" ? t("pdf.origin.gone") : null;
          const content = (
            <>
              <FileTypeIcon kind="pdf" />
              <span className="flex min-w-0 flex-1 flex-col">
                <span className="truncate text-body-md">{line.sourceName}</span>
                <span className="tabular truncate text-body-sm text-on-surface-variant">
                  {[pages, from, state].filter(Boolean).join(" · ")}
                </span>
              </span>
            </>
          );
          return (
            <li key={`${line.first}-${line.sourceNodeId}`}>
              {line.state === "live" ? (
                <button
                  type="button"
                  onClick={() => void navigate({ to: "/files/$fileId", params: { fileId: line.sourceNodeId } })}
                  className="flex w-full cursor-pointer items-center gap-3 rounded-sm px-1 py-2 text-left hover:bg-on-surface/8"
                >
                  {content}
                </button>
              ) : (
                <div className="flex items-center gap-3 px-1 py-2">{content}</div>
              )}
            </li>
          );
        })}
      </ul>
    </section>
  );
}
