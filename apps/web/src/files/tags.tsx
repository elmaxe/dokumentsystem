import { TagEditor, TagsDialog, useSnackbar, type TagInfo } from "@dokument/ui";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { drive, errorMessage, fileTagsQuery, invalidateTags, tagSuggestionsQuery, type Item } from "./drive";

/** Changes tags and refetches what shows them; each change can be undone from the snackbar. */
function useChangeTags() {
  const { t } = useTranslation();
  const toast = useSnackbar();
  const queryClient = useQueryClient();
  return async (ids: string[], change: { add?: string[]; remove?: string[] }, done: string) => {
    try {
      await drive.changeTags(ids, change);
      toast(done, {
        label: t("common.undo"),
        onClick: () =>
          void drive
            .changeTags(ids, { add: change.remove, remove: change.add })
            .catch((error: unknown) => toast(errorMessage(t, error)))
            .finally(() => void invalidateTags(queryClient)),
      });
    } catch (error) {
      toast(errorMessage(t, error));
    }
    await invalidateTags(queryClient);
  };
}

/** The Tags section of a file's Info tab (docs/design.md §13.4). Tapping a tag searches for it. */
export function FileTagsSection({ item, canEdit }: { item: Item; canEdit: boolean }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const change = useChangeTags();
  const [typed, setTyped] = useState("");
  const tags = useQuery(fileTagsQuery(item.id));
  const suggestions = useQuery(tagSuggestionsQuery(typed.trim()));
  const shown: TagInfo[] = (tags.data?.tags ?? item.node.tags).map((tag) => ({ name: tag.name, source: tag.source }));
  return (
    <TagEditor
      tags={shown}
      removed={tags.data?.removed}
      canEdit={canEdit}
      suggestions={suggestions.data ?? []}
      onQueryChange={setTyped}
      onAdd={(names) =>
        void change([item.id], { add: names }, t("toasts.tagsChanged", { count: 1 }))
      }
      onRemove={(tag) =>
        void change([item.id], { remove: [tag.name] }, t("toasts.tagRemoved", { tag: tag.name, name: item.name }))
      }
      onPutBack={(name) => void change([item.id], { add: [name] }, t("toasts.tagsChanged", { count: 1 }))}
      onOpen={(tag) => void navigate({ to: "/search", search: { tag: tag.name } })}
    />
  );
}

/** Tags for the selected files at once. */
export function BulkTagsDialog({ items, onClose }: { items: Item[]; onClose: () => void }) {
  const { t } = useTranslation();
  const change = useChangeTags();
  const [typed, setTyped] = useState("");
  const [saving, setSaving] = useState(false);
  const suggestions = useQuery(tagSuggestionsQuery(typed.trim()));
  const counts = new Map<string, TagInfo & { files: number }>();
  for (const item of items) {
    for (const tag of item.tags ?? []) {
      const key = tag.name.toLowerCase();
      const seen = counts.get(key);
      counts.set(key, seen ? { ...seen, files: seen.files + 1 } : { ...tag, files: 1 });
    }
  }
  return (
    <TagsDialog
      open={items.length > 0}
      onOpenChange={(open) => !open && onClose()}
      count={items.length}
      tags={[...counts.values()].sort((a, b) => a.name.localeCompare(b.name))}
      suggestions={suggestions.data ?? []}
      onQueryChange={setTyped}
      saving={saving}
      onSave={(edit) => {
        setSaving(true);
        void change(
          items.map((item) => item.id),
          edit,
          t("toasts.tagsChanged", { count: items.length }),
        ).finally(() => {
          setSaving(false);
          onClose();
        });
      }}
    />
  );
}
