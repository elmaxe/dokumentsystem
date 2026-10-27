import type { TagRule } from "@dokument/api";
import {
  Banner,
  Button,
  Dialog,
  EmptyState,
  FileTypeIcon,
  Highlighted,
  Icon,
  IconButton,
  Progress,
  Switch,
  TagChip,
  TextField,
  kindOf,
  useFormat,
  useSnackbar,
} from "@dokument/ui";
import { keepPreviousData, useInfiniteQuery, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { errorMessage } from "../files/drive";
import { AdminFrame } from "./AdminPage";
import { admin, tagRulePreviewPage, tagRulesQuery } from "./adminApi";

/**
 * Admin → Tags (docs/design.md §13.4): keyword rules that tag files whose text or details contain one of the words.
 * Each saves on its own and runs over every file in the background.
 */
export function AdminTagsPage() {
  const { t } = useTranslation();
  const toast = useSnackbar();
  const format = useFormat();
  const queryClient = useQueryClient();
  const rules = useQuery(tagRulesQuery);
  const [editing, setEditing] = useState<TagRule | "new" | null>(null);
  const [deleting, setDeleting] = useState<TagRule | null>(null);

  const refresh = () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: ["admin", "tag-rules"] }),
      queryClient.invalidateQueries({ queryKey: ["tags"] }),
    ]);

  const toggle = async (rule: TagRule, enabled: boolean) => {
    try {
      await admin.updateTagRule(rule.id, rule.tag, rule.words, enabled);
    } catch (error) {
      toast(errorMessage(t, error));
    }
    await refresh();
  };

  return (
    <AdminFrame title={t("admin.nav.tags")}>
      <div className="flex flex-wrap items-start gap-3">
        <p className="m-0 max-w-160 min-w-60 flex-1 text-body-md text-on-surface-variant">{t("admin.tags.intro")}</p>
        <Button icon="add" onClick={() => setEditing("new")}>
          {t("admin.tags.add")}
        </Button>
      </div>
      {rules.isError ? (
        <Banner tone="error">{t("admin.tags.failed")}</Banner>
      ) : !rules.data ? (
        <Progress label={t("admin.nav.tags")} />
      ) : rules.data.length === 0 ? (
        <EmptyState icon="sell" title={t("admin.tags.empty")} text={t("admin.tags.emptyText")} className="mt-4" />
      ) : (
        <>
          <ul className="m-0 flex list-none flex-col overflow-hidden rounded-md border border-outline-variant bg-surface-container-lowest p-0">
            {rules.data.map((rule) => (
              <li
                key={rule.id}
                className="flex items-center gap-3 border-t border-outline-variant px-4 py-3 first:border-t-0 max-sm:flex-wrap max-sm:gap-2"
              >
                <TagChip tag={{ name: rule.tag, source: "rule" }} />
                <button
                  type="button"
                  onClick={() => setEditing(rule)}
                  title={format.fullDate(rule.updatedAt)}
                  className="min-w-0 flex-1 cursor-pointer truncate text-left text-body-md text-on-surface-variant hover:underline max-sm:order-last max-sm:basis-full"
                >
                  {rule.words}
                </button>
                <span className="tabular shrink-0 text-body-sm whitespace-nowrap text-on-surface-variant max-sm:ml-auto">
                  {rule.running ? (
                    <span className="inline-flex items-center gap-1 text-warning">
                      <Icon name="progress_activity" size={16} className="animate-spin" />
                      {t("admin.tags.running")}
                    </span>
                  ) : rule.enabled ? (
                    t("admin.tags.files", { count: rule.files })
                  ) : (
                    t("admin.tags.off")
                  )}
                </span>
                <Switch
                  checked={rule.enabled}
                  onCheckedChange={(enabled) => void toggle(rule, enabled)}
                  label={t("admin.tags.enabled", { tag: rule.tag })}
                />
                <IconButton icon="edit" label={t("admin.tags.edit")} onClick={() => setEditing(rule)} />
              </li>
            ))}
          </ul>
          <p className="m-0 text-body-sm text-on-surface-variant">{t("admin.tags.note")}</p>
        </>
      )}

      <RuleDialog
        key={editing === "new" ? "new" : editing?.id}
        rule={editing}
        onClose={() => setEditing(null)}
        onDelete={(rule) => {
          setEditing(null);
          setDeleting(rule);
        }}
        onSaved={async () => {
          setEditing(null);
          toast(t("admin.tags.saved"));
          await refresh();
        }}
      />

      <Dialog
        open={!!deleting}
        onOpenChange={(open) => !open && setDeleting(null)}
        title={t("admin.tags.deleteTitle", { tag: deleting?.tag ?? "" })}
        description={t("admin.tags.deleteText")}
        width="sm"
        footer={
          <>
            <Button variant="text" onClick={() => setDeleting(null)}>
              {t("common.cancel")}
            </Button>
            <Button
              variant="danger"
              onClick={async () => {
                const rule = deleting!;
                setDeleting(null);
                try {
                  await admin.deleteTagRule(rule.id);
                  toast(t("admin.tags.deleted", { tag: rule.tag }));
                } catch (error) {
                  toast(errorMessage(t, error));
                }
                await refresh();
              }}
            >
              {t("admin.tags.delete")}
            </Button>
          </>
        }
      />
    </AdminFrame>
  );
}

/** Adds or edits a rule, with a live count and the list of files it would tag. */
function RuleDialog({
  rule,
  onClose,
  onDelete,
  onSaved,
}: {
  rule: TagRule | "new" | null;
  onClose: () => void;
  onDelete: (rule: TagRule) => void;
  onSaved: () => Promise<void>;
}) {
  const { t } = useTranslation();
  const existing = rule && rule !== "new" ? rule : null;
  const [tag, setTag] = useState(existing?.tag ?? "");
  const [words, setWords] = useState(existing?.words ?? "");
  const [typed, setTyped] = useState(words);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  // The preview follows typing after a short pause.
  useEffect(() => {
    const timer = setTimeout(() => setTyped(words), 400);
    return () => clearTimeout(timer);
  }, [words]);

  const preview = useInfiniteQuery({
    queryKey: ["admin", "tag-rule-preview", tag.trim(), typed.trim()],
    queryFn: ({ pageParam }) => admin.previewTagRule(tag.trim(), typed.trim(), pageParam),
    initialPageParam: 0,
    getNextPageParam: (last, pages) => {
      const loaded = pages.length * tagRulePreviewPage;
      return loaded < last.files ? loaded : undefined;
    },
    enabled: !!rule && typed.trim().replace(/[\s,"]/g, "").length > 0,
    placeholderData: keepPreviousData,
    retry: false,
  });
  const files = preview.data?.pages[0]?.files ?? 0;
  const matches = preview.data?.pages.flatMap((page) => page.matches) ?? [];

  const save = async () => {
    setSaving(true);
    setError(null);
    try {
      if (existing) await admin.updateTagRule(existing.id, tag, words, existing.enabled);
      else await admin.createTagRule(tag, words, true);
      await onSaved();
    } catch (failure) {
      setError(errorMessage(t, failure));
    } finally {
      setSaving(false);
    }
  };

  return (
    <Dialog
      open={!!rule}
      onOpenChange={(open) => !open && onClose()}
      title={existing ? t("admin.tags.editTitle") : t("admin.tags.newTitle")}
      footer={
        <>
          {existing && (
            <Button variant="text" className="mr-auto" onClick={() => onDelete(existing)}>
              {t("admin.tags.delete")}
            </Button>
          )}
          <Button variant="outlined" onClick={onClose}>
            {t("common.cancel")}
          </Button>
          <Button disabled={saving || !tag.trim() || !words.trim()} onClick={() => void save()}>
            {t("admin.tags.save")}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        <TextField label={t("admin.tags.tag")} value={tag} maxLength={50} onChange={(event) => setTag(event.target.value)} />
        <TextField
          label={t("admin.tags.words")}
          value={words}
          onChange={(event) => setWords(event.target.value)}
          hint={t("admin.tags.wordsHint")}
          error={error ?? undefined}
        />
        {preview.data && (
          <div className="flex flex-col gap-2 rounded-md bg-surface-container-lowest p-3" aria-live="polite">
            <span className="flex items-center gap-2 text-title-sm">
              <Icon name="preview" className="text-primary" />
              {preview.isFetching && !preview.isFetchingNextPage
                ? t("admin.tags.previewing")
                : files > 0
                  ? t("admin.tags.preview", { count: files })
                  : t("admin.tags.previewNone")}
            </span>
            {matches.length > 0 && (
              <ul className="-mx-1 flex max-h-72 flex-col gap-2 overflow-y-auto px-1">
                {matches.map((match) => (
                  <li key={match.nodeId} className="flex min-w-0 items-start gap-2 text-body-sm">
                    <FileTypeIcon kind={kindOf(match.name, match.mimeType)} size="sm" />
                    <span className="flex min-w-0 flex-1 flex-col">
                      <span className="flex min-w-0 items-baseline gap-2">
                        <span className="min-w-0 truncate font-medium text-on-surface">{match.name}</span>
                        <span className="shrink-0 text-label-sm text-on-surface-variant">{match.owner}</span>
                      </span>
                      {match.snippet && (
                        <span className="truncate text-on-surface-variant">
                          “{match.snippet.cutStart && "… "}
                          <Highlighted text={match.snippet.text} ranges={match.snippet.highlights} />
                          {match.snippet.cutEnd && " …"}”
                        </span>
                      )}
                    </span>
                  </li>
                ))}
                {preview.hasNextPage && (
                  <li>
                    <Button
                      variant="text"
                      disabled={preview.isFetchingNextPage}
                      onClick={() => void preview.fetchNextPage()}
                    >
                      {t("admin.tags.previewMore", { count: Math.min(tagRulePreviewPage, files - matches.length) })}
                    </Button>
                  </li>
                )}
              </ul>
            )}
          </div>
        )}
      </div>
    </Dialog>
  );
}
