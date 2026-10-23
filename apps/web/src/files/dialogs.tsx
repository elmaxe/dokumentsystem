import { Breadcrumbs, Button, Dialog, FileTypeIcon, Icon, TextField } from "@dokument/ui";
import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { folderQuery } from "./drive";

export type NameDialogProps = {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  label: string;
  submitLabel: string;
  initial: string;
  /** Resolves to an error message to show, or null when done. */
  onSubmit: (value: string) => Promise<string | null>;
};

/**
 * Asks for a name: New folder and Rename. The extension is left unselected so typing replaces just the name.
 * Give it a key that changes per use, so it starts from `initial` each time.
 */
export function NameDialog({ open, onOpenChange, title, label, submitLabel, initial, onSubmit }: NameDialogProps) {
  const { t } = useTranslation();
  const [value, setValue] = useState(initial);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async () => {
    if (!value.trim()) return;
    setBusy(true);
    const problem = await onSubmit(value.trim());
    setBusy(false);
    if (problem) setError(problem);
    else onOpenChange(false);
  };

  return (
    <Dialog
      open={open}
      onOpenChange={onOpenChange}
      title={title}
      width="sm"
      footer={
        <>
          <Button variant="text" onClick={() => onOpenChange(false)}>
            {t("common.cancel")}
          </Button>
          <Button onClick={() => void submit()} disabled={busy || !value.trim()}>
            {submitLabel}
          </Button>
        </>
      }
    >
      <form
        onSubmit={(event) => {
          event.preventDefault();
          void submit();
        }}
      >
        <TextField
          label={label}
          value={value}
          error={error ?? undefined}
          maxLength={255}
          onChange={(event) => {
            setValue(event.target.value);
            setError(null);
          }}
          onFocus={(event) => {
            const dot = event.target.value.lastIndexOf(".");
            event.target.setSelectionRange(0, dot > 0 ? dot : event.target.value.length);
          }}
        />
      </form>
    </Dialog>
  );
}

export type MoveDialogProps = {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** What is being moved, for the title and to keep folders from being moved into themselves. */
  items: { id: string; name: string }[];
  startIn: string | null;
  onMove: (folderId: string | null) => Promise<void>;
};

/** Picks a destination folder by browsing, starting where the items are now. Key it per use, like NameDialog. */
export function MoveDialog({ open, onOpenChange, items, startIn, onMove }: MoveDialogProps) {
  const { t } = useTranslation();
  const [folderId, setFolderId] = useState<string | null>(startIn);
  const [busy, setBusy] = useState(false);
  const listing = useQuery({ ...folderQuery(folderId), enabled: open });

  const moving = new Set(items.map((item) => item.id));
  const folders = (listing.data?.items ?? []).filter((item) => item.kind === "folder");
  const path = [{ id: "root", name: t("nav.myFiles") }, ...(listing.data?.path ?? [])];

  return (
    <Dialog
      open={open}
      onOpenChange={onOpenChange}
      title={t("dialogs.move.title", { count: items.length, name: items[0]?.name })}
      footer={
        <>
          <Button variant="text" onClick={() => onOpenChange(false)}>
            {t("common.cancel")}
          </Button>
          <Button
            disabled={busy || folderId === startIn || (folderId !== null && moving.has(folderId))}
            onClick={async () => {
              setBusy(true);
              await onMove(folderId);
              setBusy(false);
            }}
          >
            {t("dialogs.move.here")}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-2">
        <Breadcrumbs path={path} onNavigate={(id) => setFolderId(id === "root" ? null : id)} />
        <ul className="m-0 flex max-h-80 min-h-40 list-none flex-col overflow-y-auto p-0">
          {folders.length === 0 && listing.isSuccess && (
            <li className="py-6 text-center text-body-md text-on-surface-variant">{t("dialogs.move.noFolders")}</li>
          )}
          {folders.map((folder) => (
            <li key={folder.id}>
              <button
                type="button"
                disabled={moving.has(folder.id)}
                aria-label={t("dialogs.move.open", { name: folder.name })}
                onClick={() => setFolderId(folder.id)}
                className="flex h-12 w-full cursor-pointer items-center gap-3 rounded-sm px-2 text-left text-body-lg hover:bg-on-surface/8 disabled:cursor-default disabled:opacity-40"
              >
                <FileTypeIcon kind="folder" />
                <span className="min-w-0 flex-1 truncate">{folder.name}</span>
                <Icon name="chevron_right" className="text-on-surface-variant" />
              </button>
            </li>
          ))}
        </ul>
      </div>
    </Dialog>
  );
}
