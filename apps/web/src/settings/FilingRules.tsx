import type { FilingRule, FilingRun } from "@dokument/api";
import {
  ActionMenu,
  Banner,
  Button,
  Checkbox,
  Dialog,
  FileTypeIcon,
  Icon,
  IconButton,
  Switch,
  TagChip,
  TagField,
  TextField,
  Progress,
  kindOf,
  useFormat,
  useSnackbar,
  type MenuAction,
} from "@dokument/ui";
import { keepPreviousData, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { ApiError, errorMessage, tagSuggestionsQuery } from "../files/drive";
import { exampleFolder, filing, filingRulesQuery, folderProblem } from "./filing";
import { filingRuns, latestRunQuery, useUndoRun, type RunScope } from "./filingRuns";
import { RunFilingDialog } from "./RunFilingDialog";

type RuleAction = "edit" | "run" | "up" | "down" | "delete";

/**
 * Filing rules in the person's Settings (docs/design.md §13.6): when one of their files gets a tag, move it to a
 * folder, once. The first rule that matches wins, so the list can be reordered.
 */
export function FilingRules() {
  const { t } = useTranslation();
  const toast = useSnackbar();
  const queryClient = useQueryClient();
  const rules = useQuery(filingRulesQuery);
  const [editing, setEditing] = useState<FilingRule | "new" | null>(null);
  const [deleting, setDeleting] = useState<FilingRule | null>(null);
  const [running, setRunning] = useState<RunScope | null>(null);
  const latest = useQuery(latestRunQuery).data;
  const busy = latest?.status === "queued" || latest?.status === "running";
  const enabled = rules.data?.filter((rule) => rule.enabled).length ?? 0;

  const refresh = () => queryClient.invalidateQueries({ queryKey: filingRulesQuery.queryKey });
  const attempt = async (change: () => Promise<unknown>) => {
    try {
      await change();
    } catch (error) {
      toast(errorMessage(t, error));
    }
    await refresh();
  };
  const move = (index: number, by: number) => {
    const ids = rules.data!.map((rule) => rule.id);
    const [id] = ids.splice(index, 1);
    if (!id) return;
    ids.splice(index + by, 0, id);
    void attempt(() => filing.reorder(ids));
  };

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-start gap-3">
        <p className="m-0 max-w-160 min-w-60 flex-1 text-body-md text-on-surface-variant">{t("filing.intro")}</p>
        <div className="flex flex-wrap gap-2">
          <Button
            variant="outlined"
            icon="play_arrow"
            disabled={enabled === 0 || busy}
            title={busy ? t("filing.run.busy") : undefined}
            onClick={() => setRunning({ rules: enabled })}
          >
            {t("filing.run.all")}
          </Button>
          <Button icon="add" onClick={() => setEditing("new")}>
            {t("filing.add")}
          </Button>
        </div>
      </div>
      {latest && <RunStatus run={latest} />}
      {rules.isError ? (
        <Banner tone="error">{t("filing.failed")}</Banner>
      ) : rules.data && rules.data.length === 0 ? (
        <p className="m-0 text-body-md text-on-surface-variant">{t("filing.empty")}</p>
      ) : rules.data ? (
        <>
          <ol className="m-0 flex list-none flex-col overflow-hidden rounded-md border border-outline-variant bg-surface-container-lowest p-0">
            {rules.data.map((rule, index) => {
              const actions: MenuAction<RuleAction>[] = [
                { id: "edit", label: t("filing.edit"), icon: "edit" },
                { id: "run", label: t("filing.run.one"), icon: "play_arrow", disabled: !rule.enabled || busy },
                { id: "up", label: t("filing.moveUp"), icon: "arrow_upward", disabled: index === 0 },
                {
                  id: "down",
                  label: t("filing.moveDown"),
                  icon: "arrow_downward",
                  disabled: index === rules.data.length - 1,
                },
                { id: "delete", label: t("filing.delete"), icon: "delete", danger: true, separatorBefore: true },
              ];
              return (
                <li
                  key={rule.id}
                  className="flex items-center gap-3 border-t border-outline-variant py-3 pr-2 pl-4 first:border-t-0"
                >
                  <span className="tabular w-5 shrink-0 text-body-md text-on-surface-variant max-sm:hidden">
                    {index + 1}
                  </span>
                  <button
                    type="button"
                    onClick={() => setEditing(rule)}
                    className="flex min-w-0 flex-1 cursor-pointer flex-wrap items-center gap-x-3 gap-y-1 text-left"
                  >
                    <TagChip tag={{ name: rule.tag, source: "user" }} />
                    <Icon name="arrow_forward" className="text-outline max-sm:hidden" />
                    <span className="flex min-w-0 flex-col">
                      <span className={`flex items-center gap-1.5 text-title-sm ${rule.enabled ? "text-on-surface" : "text-on-surface-variant"}`}>
                        <Icon name="folder" size={18} className="text-on-surface-variant" />
                        <span className="truncate">{templateLabel(t, rule.folder)}</span>
                      </span>
                      <span className="tabular text-body-sm text-on-surface-variant">
                        {rule.running ? (
                          <span className="inline-flex items-center gap-1 text-warning">
                            <Icon name="progress_activity" size={16} className="animate-spin" />
                            {t("filing.running")}
                          </span>
                        ) : !rule.enabled ? (
                          t("filing.off")
                        ) : (
                          <>
                            {t("filing.filedCount", { count: rule.filed, folder: exampleFolder(rule.folder) })}
                            {rule.skipped > 0 && (
                              <span className="flex items-center gap-1">
                                <Icon name="group" size={16} />
                                {t("filing.skipped", { count: rule.skipped })}
                              </span>
                            )}
                          </>
                        )}
                      </span>
                    </span>
                  </button>
                  <Switch
                    checked={rule.enabled}
                    onCheckedChange={(enabled) =>
                      void attempt(() => filing.update(rule.id, { tag: rule.tag, folder: rule.folder, enabled }))
                    }
                    label={t("filing.enabled", { tag: rule.tag })}
                  />
                  <ActionMenu
                    trigger={<IconButton icon="more_vert" label={t("filing.more", { tag: rule.tag })} />}
                    actions={actions}
                    onAction={(id) => {
                      if (id === "edit") setEditing(rule);
                      else if (id === "delete") setDeleting(rule);
                      else if (id === "run") setRunning({ ruleId: rule.id, ruleTag: rule.tag });
                      else move(index, id === "up" ? -1 : 1);
                    }}
                  />
                </li>
              );
            })}
          </ol>
          <p className="m-0 text-body-sm text-on-surface-variant">{t("filing.note")}</p>
        </>
      ) : null}

      <RunFilingDialog
        key={running ? JSON.stringify(running) : "none"}
        scope={running}
        onClose={() => setRunning(null)}
      />

      <RuleDialog
        key={editing === "new" ? "new" : editing?.id}
        rule={editing}
        onClose={() => setEditing(null)}
        onSaved={async () => {
          setEditing(null);
          toast(t("filing.saved"));
          await refresh();
        }}
      />

      <Dialog
        open={!!deleting}
        onOpenChange={(open) => !open && setDeleting(null)}
        title={t("filing.deleteTitle", { tag: deleting?.tag ?? "" })}
        description={t("filing.deleteText")}
        width="sm"
        footer={
          <>
            <Button variant="text" onClick={() => setDeleting(null)}>
              {t("common.cancel")}
            </Button>
            <Button
              variant="danger"
              onClick={() => {
                const rule = deleting!;
                setDeleting(null);
                void attempt(async () => {
                  await filing.remove(rule.id);
                  toast(t("filing.deleted", { tag: rule.tag }));
                });
              }}
            >
              {t("filing.delete")}
            </Button>
          </>
        }
      />
    </div>
  );
}

/** A run that is going, with its progress and Stop, or the last one while it can still be undone. */
function RunStatus({ run }: { run: FilingRun }) {
  const { t } = useTranslation();
  const toast = useSnackbar();
  const format = useFormat();
  const queryClient = useQueryClient();
  const undo = useUndoRun();
  const going = run.status === "queued" || run.status === "running";

  if (going) {
    const counting = run.status === "queued" || run.total === 0;
    return (
      <div className="flex flex-col gap-2 rounded-md bg-surface-container-lowest px-4 py-3" aria-live="polite">
        <div className="flex items-center gap-3">
          <Icon name="progress_activity" className="animate-spin text-primary" />
          <span className="tabular flex-1 text-title-sm">
            {counting ? t("filing.run.looking") : t("filing.run.progress", { done: run.done, total: run.total })}
          </span>
          <Button
            variant="text"
            disabled={run.stopRequested}
            onClick={async () => {
              try {
                await filingRuns.stop(run.id);
              } catch (error) {
                toast(errorMessage(t, error));
              }
              await queryClient.invalidateQueries({ queryKey: latestRunQuery.queryKey });
            }}
          >
            {run.stopRequested ? t("filing.run.stopping") : t("filing.run.stop")}
          </Button>
        </div>
        <Progress value={counting ? undefined : run.total === 0 ? 0 : (run.done * 100) / run.total} label={t("filing.run.progress", { done: run.done, total: run.total })} />
      </div>
    );
  }

  if (!run.canUndo) return null;
  return (
    <div className="flex flex-wrap items-center gap-x-3 gap-y-1 rounded-md bg-surface-container-lowest py-2 pr-2 pl-4">
      <Icon name="history" className="text-on-surface-variant" />
      <span className="min-w-0 flex-1 text-body-md text-on-surface-variant">
        {t("filing.run.last", { count: run.moved, when: format.date(run.finishedAt ?? run.createdAt, { inSentence: true }) })}
      </span>
      <Button variant="text" icon="undo" onClick={() => void undo(run)}>
        {t("filing.run.undo")}
      </Button>
    </div>
  );
}

/** "Kvitton/{year}" as the list shows it: "Kvitton / year", in the person's language. */
function templateLabel(t: ReturnType<typeof useTranslation>["t"], template: string) {
  return template
    .split("/")
    .map((folder) =>
      folder.replace(/\{(year|month)\}/gi, (_, part: string) =>
        part.toLowerCase() === "year" ? t("filing.year") : t("filing.month"),
      ),
    )
    .join(" / ");
}

/** Adds or edits a rule, with where a couple of the files that have the tag now would go. */
function RuleDialog({
  rule,
  onClose,
  onSaved,
}: {
  rule: FilingRule | "new" | null;
  onClose: () => void;
  onSaved: () => Promise<void>;
}) {
  const { t } = useTranslation();
  const folderRef = useRef<HTMLInputElement>(null);
  const existing = rule && rule !== "new" ? rule : null;
  const [tag, setTag] = useState(existing?.tag ?? "");
  const [folder, setFolder] = useState(existing?.folder ?? "");
  const [fileExisting, setFileExisting] = useState(!existing);
  const [typed, setTyped] = useState({ tag, folder });
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const problem = folder.trim() ? folderProblem(folder) : null;

  useEffect(() => {
    const timer = setTimeout(() => setTyped({ tag: tag.trim(), folder: folder.trim() }), 400);
    return () => clearTimeout(timer);
  }, [tag, folder]);

  const suggestions = useQuery({ ...tagSuggestionsQuery(tag.trim(), 8), enabled: !!rule });
  const preview = useQuery({
    queryKey: ["filing-rule-preview", typed.tag, typed.folder],
    queryFn: () => filing.preview(typed.tag, typed.folder),
    enabled: !!rule && typed.tag.length > 0,
    placeholderData: keepPreviousData,
    retry: false,
  });
  const files = preview.data?.files ?? 0;

  const insert = (placeholder: string) => {
    const input = folderRef.current;
    const at = input?.selectionStart ?? folder.length;
    const end = input?.selectionEnd ?? at;
    const before = folder.slice(0, at);
    const glue = before && !before.endsWith("/") ? "/" : "";
    const next = before + glue + placeholder + folder.slice(end);
    setFolder(next);
    requestAnimationFrame(() => {
      input?.focus();
      const caret = (before + glue + placeholder).length;
      input?.setSelectionRange(caret, caret);
    });
  };

  const save = async () => {
    setSaving(true);
    setError(null);
    try {
      const body = { tag, folder, enabled: existing?.enabled ?? true, fileExisting: fileExisting && files > 0 };
      if (existing) await filing.update(existing.id, body);
      else await filing.create(body);
      await onSaved();
    } catch (failure) {
      setError(
        failure instanceof ApiError && failure.problem.code === "nameTaken"
          ? t("filing.taken", { tag: tag.trim() })
          : errorMessage(t, failure),
      );
    } finally {
      setSaving(false);
    }
  };

  return (
    <Dialog
      open={!!rule}
      onOpenChange={(open) => !open && onClose()}
      title={existing ? t("filing.editTitle") : t("filing.newTitle")}
      footer={
        <>
          <Button variant="outlined" onClick={onClose}>
            {t("common.cancel")}
          </Button>
          <Button disabled={saving || !tag.trim() || !folder.trim() || !!problem} onClick={() => void save()}>
            {t("filing.save")}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        <TagField
          label={t("filing.tag")}
          value={tag}
          onChange={setTag}
          suggestions={suggestions.data ?? []}
          autoFocus={!existing}
        />
        <div className="flex flex-col gap-2">
          <TextField
            ref={folderRef}
            label={t("filing.folder")}
            icon="folder"
            value={folder}
            placeholder={t("filing.folderPlaceholder")}
            onChange={(event) => setFolder(event.target.value)}
            hint={t("filing.folderHint")}
            error={
              problem === "placeholder"
                ? t("filing.badPlaceholder")
                : problem === "empty"
                  ? t("filing.emptyFolder")
                  : (error ?? undefined)
            }
          />
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-body-sm text-on-surface-variant">{t("filing.insert")}</span>
            <Button variant="outlined" icon="add" onClick={() => insert("{year}")}>
              {t("filing.year")}
            </Button>
            <Button variant="outlined" icon="add" onClick={() => insert("{month}")}>
              {t("filing.month")}
            </Button>
          </div>
        </div>
        {preview.data && preview.data.examples.length > 0 && !problem && (
          <div className="flex flex-col gap-2 rounded-md bg-surface-container-lowest p-3" aria-live="polite">
            <span className="flex items-center gap-2 text-title-sm">
              <Icon name="preview" className="text-primary" />
              {t("filing.example")}
            </span>
            {preview.data.examples.map((example) => (
              <span key={example.nodeId} className="flex min-w-0 items-center gap-2 text-body-sm">
                <FileTypeIcon kind={kindOf(example.name, example.mimeType)} size="sm" />
                <span className="min-w-0 flex-1 truncate font-medium text-on-surface">{example.name}</span>
                <span className="shrink-0 truncate text-on-surface-variant">{example.folder}</span>
              </span>
            ))}
          </div>
        )}
        {files > 0 && (
          <label className="flex cursor-pointer items-start gap-3">
            <span className="pt-0.5">
              <Checkbox
                checked={fileExisting}
                onCheckedChange={setFileExisting}
                label={t("filing.fileExisting", { count: files })}
              />
            </span>
            <span className="flex flex-col">
              <span className="text-title-sm text-on-surface">{t("filing.fileExisting", { count: files })}</span>
              <span className="text-body-sm text-on-surface-variant">{t("filing.fileExistingHint")}</span>
            </span>
          </label>
        )}
      </div>
    </Dialog>
  );
}
