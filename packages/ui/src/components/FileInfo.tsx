import { useId, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { cn } from "../lib/cn";
import { useFormat } from "../lib/display";
import {
  currentEdit,
  formatDuration,
  formatTaken,
  takenPattern,
  type DetailsEdit,
  type FileDetailsView,
  type FileTextView,
  type TextPageSource,
  type TextPageView,
} from "../lib/fileInfo";
import { Button, IconButton } from "./Button";
import { Dialog } from "./Dialog";
import { Icon } from "./Icon";
import { TextField } from "./TextField";

const sourceIcons: Record<TextPageSource, string> = {
  text: "article",
  ocr: "document_scanner",
  added: "edit_note",
  metadata: "info",
};

/** A small label on a detail or a page: where text came from, or that someone edited it. */
function Tag({ icon, tone = "neutral", children }: { icon?: string; tone?: "neutral" | "edited"; children: ReactNode }) {
  return (
    <span
      className={cn(
        "inline-flex h-6 items-center gap-1 rounded-xs px-2 text-label-md whitespace-nowrap",
        tone === "edited"
          ? "bg-primary-container text-on-primary-container"
          : "bg-secondary-container text-on-secondary-container",
      )}
    >
      {icon && <Icon name={icon} size={14} />}
      {children}
    </span>
  );
}

function SectionHead({ id, title, action }: { id: string; title: string; action?: ReactNode }) {
  return (
    <div className="flex min-h-10 items-center gap-2">
      <h3 id={id} className="m-0 flex-1 text-title-sm text-on-surface">
        {title}
      </h3>
      {action}
    </div>
  );
}

function Row({ label, edited, children }: { label: string; edited?: boolean; children: ReactNode }) {
  const { t } = useTranslation();
  return (
    <div className="flex flex-col gap-0.5 py-2">
      <dt className="flex items-center gap-2 text-body-sm text-on-surface-variant">
        {label}
        {edited && <Tag tone="edited">{t("fileInfo.edited")}</Tag>}
      </dt>
      <dd className="m-0 text-body-md break-words whitespace-pre-line text-on-surface">{children}</dd>
    </div>
  );
}

/** "Read from file: …  Use this", under an edited detail. */
function ReadFromFile({ value, onUse }: { value: string; onUse?: () => void }) {
  const { t } = useTranslation();
  return (
    <dd className="m-0 -mt-1.5 flex flex-wrap items-center gap-x-2 pb-2 text-body-sm text-on-surface-variant">
      <span className="min-w-0 break-words">{t("fileInfo.readFromFile", { value })}</span>
      {onUse && (
        <button type="button" onClick={onUse} className="cursor-pointer font-medium text-primary hover:underline">
          {t("fileInfo.useThis")}
        </button>
      )}
    </dd>
  );
}

export type DetailsSectionProps = {
  details: FileDetailsView;
  /** Saves the title, description and date taken; leave out for people who can't edit. Rejects with a message to show. */
  onSave?: (edit: DetailsEdit) => Promise<void>;
};

/** What the file says about itself, with people's edits on top (§13.5). */
export function DetailsSection({ details, onSave }: DetailsSectionProps) {
  const { t, i18n } = useTranslation();
  const format = useFormat();
  const headingId = useId();
  const [editing, setEditing] = useState(false);
  const taken = (value: FileDetailsView["taken"]["value"]) => (value ? formatTaken(t, i18n.language, value) : null);
  const save = onSave && ((change: Partial<DetailsEdit>) => onSave({ ...currentEdit(details), ...change }));

  if (editing && save) {
    return <DetailsForm details={details} headingId={headingId} onCancel={() => setEditing(false)} onSave={async (edit) => {
      await onSave!(edit);
      setEditing(false);
    }} />;
  }

  const { title, description } = details;
  const hasAnything =
    title.value || description.value || details.taken.value || details.camera || details.lens || details.width ||
    details.durationSeconds || details.pageCount || details.place || details.fields.length > 0 ||
    title.edited || description.edited;

  return (
    <section aria-labelledby={headingId} className="mt-2 border-t border-outline-variant pt-2">
      <SectionHead
        id={headingId}
        title={t("fileInfo.details")}
        action={
          save && (
            <Button variant="text" icon="edit" onClick={() => setEditing(true)}>
              {t("fileInfo.edit")}
            </Button>
          )
        }
      />
      {!hasAnything && (
        <p className="m-0 pb-2 text-body-sm text-on-surface-variant">
          {details.reading ? t("fileInfo.readingDetails") : t("fileInfo.noDetails")}
        </p>
      )}
      <dl className="m-0 flex flex-col">
        {(title.value || title.edited) && (
          <div className="flex flex-col">
            <Row label={t("fileInfo.fields.title")} edited={title.edited}>{title.value ?? ""}</Row>
            {title.edited && title.read && (
              <ReadFromFile value={title.read} onUse={save && (() => void save({ title: null }))} />
            )}
          </div>
        )}
        {(description.value || description.edited) && (
          <div className="flex flex-col">
            <Row label={t("fileInfo.fields.description")} edited={description.edited}>{description.value ?? ""}</Row>
            {description.edited && description.read && (
              <ReadFromFile value={description.read} onUse={save && (() => void save({ description: null }))} />
            )}
          </div>
        )}
        {details.taken.value && (
          <div className="flex flex-col">
            <Row label={t("fileInfo.fields.taken")} edited={details.taken.edited}>
              <span className="tabular">{taken(details.taken.value)}</span>
            </Row>
            {details.taken.edited && details.taken.read && (
              <ReadFromFile
                value={formatTaken(t, i18n.language, details.taken.read)}
                onUse={save && (() => void save({ taken: null }))}
              />
            )}
          </div>
        )}
        {details.camera && <Row label={t("fileInfo.fields.camera")}>{details.camera}</Row>}
        {details.lens && <Row label={t("fileInfo.fields.lens")}>{details.lens}</Row>}
        {details.width && details.height && (
          <Row label={t("fileInfo.fields.dimensions")}>
            <span className="tabular">{t("fileInfo.pixels", { width: details.width, height: details.height })}</span>
          </Row>
        )}
        {details.durationSeconds != null && (
          <Row label={t("fileInfo.fields.duration")}>
            <span className="tabular">{formatDuration(details.durationSeconds)}</span>
          </Row>
        )}
        {details.pageCount != null && (
          <Row label={t("fileInfo.fields.pages")}>
            <span className="tabular">{details.pageCount}</span>
          </Row>
        )}
        {details.place && (
          <Row label={t("fileInfo.fields.place")}>
            <span className="tabular">
              {details.place.latitude.toFixed(4)}, {details.place.longitude.toFixed(4)}
            </span>
            {" · "}
            <a
              href={`https://www.openstreetmap.org/?mlat=${details.place.latitude}&mlon=${details.place.longitude}#map=16/${details.place.latitude}/${details.place.longitude}`}
              target="_blank"
              rel="noreferrer noopener"
              className="font-medium text-primary hover:underline"
            >
              {t("fileInfo.openMap")}
            </a>
          </Row>
        )}
        {details.fields.map((field) => (
          <Row key={field.key} label={t(`fileInfo.fields.${field.key}`, { defaultValue: field.key })}>
            {field.value}
          </Row>
        ))}
      </dl>
      {details.editedBy && details.editedAt && (
        <p className="m-0 flex gap-1.5 pt-1 text-body-sm text-on-surface-variant">
          <Icon name="info" size={16} className="mt-0.5 shrink-0" />
          {t("fileInfo.editedBy", { who: details.editedBy, time: format.date(details.editedAt, { inSentence: true }) })}
        </p>
      )}
    </section>
  );
}

function DetailsForm({
  details,
  headingId,
  onCancel,
  onSave,
}: {
  details: FileDetailsView;
  headingId: string;
  onCancel: () => void;
  onSave: (edit: DetailsEdit) => Promise<void>;
}) {
  const { t, i18n } = useTranslation();
  const [title, setTitle] = useState(details.title.value ?? "");
  const [description, setDescription] = useState(details.description.value ?? "");
  const [taken, setTaken] = useState(details.taken.value?.value ?? "");
  const [error, setError] = useState<string>();
  const [saving, setSaving] = useState(false);
  const descriptionId = useId();
  const takenValid = taken.trim() === "" || takenPattern.test(taken.trim());

  const submit = async () => {
    if (!takenValid) return;
    setSaving(true);
    setError(undefined);
    try {
      await onSave({ title, description, taken: taken.trim() || null });
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
      setSaving(false);
    }
  };

  return (
    <section aria-labelledby={headingId} className="mt-2 border-t border-outline-variant pt-2">
      <SectionHead id={headingId} title={t("fileInfo.editDetails")} />
      <form
        className="flex flex-col gap-4 pt-1"
        onSubmit={(event) => {
          event.preventDefault();
          void submit();
        }}
      >
        <TextField label={t("fileInfo.fields.title")} value={title} onChange={(e) => setTitle(e.target.value)} maxLength={500} />
        <div className="flex flex-col gap-1">
          <label htmlFor={descriptionId} className="text-label-lg text-on-surface">
            {t("fileInfo.fields.description")}
          </label>
          <textarea
            id={descriptionId}
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            maxLength={4000}
            rows={3}
            className="min-h-20 resize-y rounded-sm border border-outline bg-surface-container-lowest px-3 py-2 text-body-md text-on-surface outline-none focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus-ring"
          />
        </div>
        <div className="flex flex-col gap-1">
          <TextField
            label={t("fileInfo.fields.taken")}
            value={taken}
            onChange={(e) => setTaken(e.target.value)}
            placeholder="1974-07-14 15:30"
            inputMode="numeric"
            className="tabular"
            error={takenValid ? undefined : t("fileInfo.takenInvalid")}
            hint={t("fileInfo.takenHint")}
          />
          {details.taken.read && (
            <p className="m-0 text-body-sm text-on-surface-variant">
              {t("fileInfo.readFromFile", { value: formatTaken(t, i18n.language, details.taken.read) })}
            </p>
          )}
        </div>
        {(details.camera || details.lens || details.width) && (
          <p className="m-0 flex gap-1.5 text-body-sm text-on-surface-variant">
            <Icon name="lock" size={16} className="mt-0.5 shrink-0" />
            {t("fileInfo.lockedHint")}
          </p>
        )}
        {error && (
          <p role="alert" className="m-0 flex items-start gap-1 text-body-sm text-error">
            <Icon name="error" size={16} filled />
            {error}
          </p>
        )}
        <div className="flex justify-end gap-2">
          <Button variant="outlined" onClick={onCancel}>
            {t("common.cancel")}
          </Button>
          <Button type="submit" disabled={saving || !takenValid}>
            {t("common.save")}
          </Button>
        </div>
      </form>
    </section>
  );
}

export type TextSectionProps = {
  text: FileTextView;
  /** Opens every page; with a page number, opens that page for editing. */
  onShowAll: (editPage?: number) => void;
  canEdit?: boolean;
};

/** The start of the file's searchable text and where it came from (§13.5). Hidden for formats without text. */
export function TextSection({ text, onShowAll, canEdit }: TextSectionProps) {
  const { t } = useTranslation();
  const headingId = useId();
  if (text.state === "unsupported" && text.totalPages === 0) return null;

  const first = text.pages[0];
  const sources = [...new Set(text.pages.map((p) => p.source))];
  const status =
    text.state === "reading"
      ? t("fileInfo.readingText")
      : text.state === "unreadable" && text.totalPages === 0
        ? t("fileInfo.unreadableText")
        : text.totalPages === 0
          ? text.ocrSkipped
            ? t("fileInfo.ocrOff")
            : t("fileInfo.noText")
          : null;

  return (
    <section aria-labelledby={headingId} className="mt-2 border-t border-outline-variant pt-2">
      <SectionHead
        id={headingId}
        title={t("fileInfo.text")}
        action={
          canEdit &&
          text.state !== "reading" &&
          (text.totalPages === 0 ? (
            <Button variant="text" icon="add" onClick={() => onShowAll(1)}>
              {t("fileInfo.addText")}
            </Button>
          ) : (
            <Button variant="text" icon="edit" onClick={() => onShowAll(text.totalPages === 1 ? first?.pageNo : undefined)}>
              {t("fileInfo.edit")}
            </Button>
          ))
        }
      />
      {status && (
        <p className="m-0 flex gap-1.5 pb-2 text-body-sm text-on-surface-variant">
          {text.state === "reading" && <Icon name="hourglass_top" size={16} className="mt-0.5 shrink-0" />}
          {status}
        </p>
      )}
      {first && (
        <>
          <div className="flex flex-wrap gap-1.5 pb-2">
            {sources.map((source) => (
              <Tag key={source} icon={sourceIcons[source]}>
                {t(`fileInfo.sources.${source}`)}
              </Tag>
            ))}
            {text.editedPages > 0 && sources.some((s) => s !== "added") && (
              <Tag tone="edited" icon="edit">
                {t("fileInfo.pagesEdited", { count: text.editedPages })}
              </Tag>
            )}
            <Tag>
              <span className="tabular">{t("fileInfo.words", { count: text.words })}</span>
            </Tag>
          </div>
          <div className="relative max-h-36 overflow-hidden rounded-sm border border-outline-variant bg-surface-container-lowest px-3 py-2.5 text-body-md whitespace-pre-line text-on-surface after:absolute after:inset-x-0 after:bottom-0 after:h-9 after:bg-linear-to-b after:from-transparent after:to-surface-container-lowest">
            {first.text.slice(0, 600)}
          </div>
          <Button variant="text" icon="notes" onClick={() => onShowAll()} className="-ml-3 mt-1">
            {text.totalPages > 1 ? t("fileInfo.showAllPages", { count: text.totalPages }) : t("fileInfo.showAllText")}
          </Button>
        </>
      )}
    </section>
  );
}

export type TextPager = { page: number; total: number; onPage: (page: number) => void };

export type TextDialogProps = {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  name: string;
  text: FileTextView;
  /** Every page with text, in order; with a pager, the pages loaded for the page shown. */
  pages: TextPageView[];
  /** Whether the file has numbered pages (a PDF) or slides, rather than one text. */
  paging?: "pages" | "slides";
  /** For long files: shows one page at a time, with buttons to turn pages. */
  pager?: TextPager;
  /** The pager's page is still loading. */
  loading?: boolean;
  onCopy: () => void;
  /** Leave out for people who can't edit. Both reject with a message to show. */
  onSavePage?: (pageNo: number, text: string) => Promise<void>;
  onRevertPage?: (pageNo: number) => Promise<void>;
  /** Opens with this page in edit mode. */
  editPage?: number;
};

/** Every page of a file's searchable text, where each came from, and editing one page at a time (§13.5). */
export function TextDialog({
  open,
  onOpenChange,
  name,
  text,
  pages,
  paging,
  pager,
  loading,
  onCopy,
  onSavePage,
  onRevertPage,
  editPage,
}: TextDialogProps) {
  const { t } = useTranslation();
  const [editing, setEditing] = useState<number | undefined>(editPage);
  const [opened, setOpened] = useState(open);
  if (open !== opened) {
    setOpened(open);
    if (open) setEditing(editPage);
  }

  // A page where nothing was read yet shows as an empty page to write in.
  const blank = (pageNo: number): TextPageView => ({ pageNo, source: "added", text: "", edited: false });
  const shown: TextPageView[] = pager
    ? loading
      ? []
      : [pages.find((p) => p.pageNo === pager.page) ?? blank(pager.page)]
    : editing !== undefined && !pages.some((p) => p.pageNo === editing)
      ? [...pages, blank(editing)].sort((a, b) => a.pageNo - b.pageNo)
      : pages;
  const turn = (page: number) => {
    setEditing(undefined);
    pager?.onPage(page);
  };
  const summary = [
    text.pageCount ? t(paging === "slides" ? "fileInfo.slideCount" : "fileInfo.pageCount", { count: text.pageCount }) : null,
    t("fileInfo.words", { count: text.words }),
    text.editedPages > 0 ? t("fileInfo.pagesEdited", { count: text.editedPages }) : null,
  ].filter(Boolean);

  return (
    <Dialog
      open={open}
      onOpenChange={onOpenChange}
      width="lg"
      title={t("fileInfo.textTitle", { name })}
      description={<span className="tabular">{summary.join(" · ")}</span>}
      headAction={<IconButton icon="content_copy" label={t("fileInfo.copyAll")} onClick={onCopy} />}
      footer={pager && <Pager pager={pager} slides={paging === "slides"} onTurn={turn} />}
    >
      {loading && <p className="m-0 pb-4 text-body-md text-on-surface-variant">{t("fileInfo.loadingPage")}</p>}
      {/* A steady height keeps the page buttons in place while turning pages. */}
      <ol className={cn("m-0 flex list-none flex-col gap-3 p-0 pb-4", pager && "sm:min-h-[50vh]")}>
        {shown.map((page) => (
          <TextPageCard
            key={page.pageNo}
            page={page}
            label={paging ? t(paging === "slides" ? "fileInfo.slide" : "fileInfo.page", { number: page.pageNo }) : undefined}
            editing={editing === page.pageNo}
            onEdit={onSavePage && (() => setEditing(page.pageNo))}
            onCancel={() => setEditing(undefined)}
            onSave={
              onSavePage &&
              (async (value) => {
                await onSavePage(page.pageNo, value);
                setEditing(undefined);
              })
            }
            onRevert={
              onRevertPage &&
              page.edited &&
              page.source !== "added" &&
              (async () => {
                await onRevertPage(page.pageNo);
                setEditing(undefined);
              })
            }
          />
        ))}
      </ol>
    </Dialog>
  );
}

/** Previous and next, and a field to go straight to a page. */
function Pager({ pager, slides, onTurn }: { pager: TextPager; slides: boolean; onTurn: (page: number) => void }) {
  const { t } = useTranslation();
  const fieldId = useId();
  const [value, setValue] = useState(String(pager.page));
  const [shownPage, setShownPage] = useState(pager.page);
  if (pager.page !== shownPage) {
    setShownPage(pager.page);
    setValue(String(pager.page));
  }
  const go = () => {
    const page = Number(value);
    if (Number.isInteger(page) && page >= 1 && page <= pager.total && page !== pager.page) onTurn(page);
    else setValue(String(pager.page));
  };

  return (
    <nav aria-label={t(slides ? "fileInfo.slides" : "fileInfo.pages")} className="flex w-full items-center justify-center gap-1">
      <IconButton
        icon="chevron_left"
        label={t(slides ? "fileInfo.previousSlide" : "fileInfo.previousPage")}
        disabled={pager.page <= 1}
        onClick={() => onTurn(pager.page - 1)}
      />
      <label htmlFor={fieldId} className="sr-only">
        {t(slides ? "fileInfo.goToSlide" : "fileInfo.goToPage")}
      </label>
      <span className="tabular flex items-center gap-2 px-1 text-label-lg text-on-surface">
        <input
          id={fieldId}
          inputMode="numeric"
          value={value}
          onChange={(e) => setValue(e.target.value.replace(/\D/g, ""))}
          onBlur={go}
          onKeyDown={(e) => {
            if (e.key === "Enter") go();
          }}
          className="h-9 w-14 rounded-sm border border-outline bg-surface-container-lowest text-center text-on-surface outline-none focus:border-2 focus:border-primary"
        />
        {t("fileInfo.ofTotal", { total: pager.total })}
      </span>
      <IconButton
        icon="chevron_right"
        label={t(slides ? "fileInfo.nextSlide" : "fileInfo.nextPage")}
        disabled={pager.page >= pager.total}
        onClick={() => onTurn(pager.page + 1)}
      />
    </nav>
  );
}

function TextPageCard({
  page,
  label,
  editing,
  onEdit,
  onCancel,
  onSave,
  onRevert,
}: {
  page: TextPageView;
  label?: string;
  editing: boolean;
  onEdit?: () => void;
  onCancel: () => void;
  onSave?: (text: string) => Promise<void>;
  onRevert?: false | (() => Promise<void>);
}) {
  const { t } = useTranslation();
  const [value, setValue] = useState(page.text);
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);
  const fieldId = useId();
  const run = async (action: () => Promise<void>) => {
    setBusy(true);
    setError(undefined);
    try {
      await action();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <li className="flex flex-col gap-2 rounded-md bg-surface-container-lowest px-4 py-3 text-on-surface">
      <div className="flex min-h-8 flex-wrap items-center gap-2">
        {label && <span className="text-label-lg text-on-surface">{label}</span>}
        {page.text && <Tag icon={sourceIcons[page.source]}>{t(`fileInfo.sources.${page.source}`)}</Tag>}
        {page.edited && page.source !== "added" && (
          <Tag tone="edited">{page.editedBy ? t("fileInfo.editedByShort", { who: page.editedBy }) : t("fileInfo.edited")}</Tag>
        )}
        <span className="flex-1" />
        {!editing && onEdit && (
          <Button
            variant="text"
            icon="edit"
            onClick={() => {
              setValue(page.text);
              onEdit();
            }}
          >
            {t("fileInfo.editPage")}
          </Button>
        )}
      </div>
      {editing ? (
        <>
          <label htmlFor={fieldId} className="sr-only">
            {label ?? t("fileInfo.text")}
          </label>
          <textarea
            id={fieldId}
            autoFocus
            value={value}
            onChange={(e) => setValue(e.target.value)}
            rows={Math.min(14, Math.max(5, value.split("\n").length + 1))}
            className="w-full resize-y rounded-sm border-2 border-primary bg-surface-container-lowest px-3 py-2 text-body-md text-on-surface outline-none"
          />
          <p className="m-0 text-body-sm text-on-surface-variant">{t("fileInfo.textHint")}</p>
          {error && (
            <p role="alert" className="m-0 flex items-start gap-1 text-body-sm text-error">
              <Icon name="error" size={16} filled />
              {error}
            </p>
          )}
          <div className="flex flex-wrap items-center gap-2">
            {onRevert && (
              <Button variant="text" icon="undo" disabled={busy} onClick={() => void run(onRevert)} className="-ml-3">
                {t("fileInfo.useReadText")}
              </Button>
            )}
            <span className="flex-1" />
            <Button variant="outlined" onClick={onCancel}>
              {t("common.cancel")}
            </Button>
            <Button disabled={busy} onClick={() => onSave && void run(() => onSave(value))}>
              {t("common.save")}
            </Button>
          </div>
        </>
      ) : page.text.trim() ? (
        <p className="m-0 text-body-md break-words whitespace-pre-line">{page.text}</p>
      ) : (
        <p className="m-0 text-body-md text-on-surface-variant">{t("fileInfo.noTextOnPage")}</p>
      )}
    </li>
  );
}
