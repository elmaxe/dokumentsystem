import type { TrashItem } from "@dokument/api";
import { formatBytes, formatFileDate } from "@dokument/core";
import {
  ActionBar,
  Banner,
  Button,
  Dialog,
  EmptyState,
  FileList,
  Icon,
  isFolder,
  useSnackbar,
  type FileColumn,
  type MenuAction,
} from "@dokument/ui";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { drive, errorMessage, invalidateTrash, toItem, trashQuery, type Item } from "./drive";

type Row = Item & { trash: TrashItem };

const day = 24 * 60 * 60 * 1000;

/** The last week before the purge job deletes something is shown in amber, with an icon (§16.1). */
const soonDays = 7;

type Confirm = { kind: "delete"; rows: Row[] } | { kind: "empty" } | { kind: "open"; row: Row };

/**
 * Trash (docs/design.md §12): what the user trashed, where it came from and when it's deleted forever. Restore puts
 * things back; Delete forever and Empty trash confirm first. Trashed items can't be opened until they're restored.
 */
export function TrashPage() {
  const { t, i18n } = useTranslation();
  const toast = useSnackbar();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const listing = useQuery(trashQuery);
  const [selected, setSelected] = useState<string[]>([]);
  const [confirm, setConfirm] = useState<Confirm | null>(null);
  const [busy, setBusy] = useState(false);
  // Days left only need to be right to the day, so the clock is read once per visit.
  const [now] = useState(() => Date.now());

  const rows: Row[] = (listing.data?.items ?? []).map((trash) => ({
    ...toItem(trash.node),
    size: trash.size,
    trash,
  }));
  const byId = new Map(rows.map((row) => [row.id, row]));
  const chosen = selected.map((id) => byId.get(id)).filter((row): row is Row => !!row);
  const keepDays = listing.data?.keepDays ?? 30;

  const daysLeft = (row: Row) => Math.max(0, Math.ceil((Date.parse(row.trash.deleteAt) - now) / day));
  const countdown = (row: Row) => {
    const days = daysLeft(row);
    return days === 0 ? t("trash.deletesToday") : t("trash.deletesIn", { count: days });
  };
  const from = (row: Row) =>
    row.trash.path.length === 0 ? t("nav.myFiles") : row.trash.path.map((p) => p.name).join(" / ");
  const warning = (row: Row) => (
    <span className="inline-flex items-center gap-1 text-warning">
      <Icon name="schedule" size={16} />
      {countdown(row)}
    </span>
  );

  const columns: FileColumn<Row>[] = [
    {
      id: "from",
      label: t("trash.columns.from"),
      className: "w-[26%] truncate @max-[600px]:hidden",
      cell: from,
    },
    {
      id: "deleted",
      label: t("trash.columns.deleted"),
      className: "w-[24%] @max-[600px]:hidden",
      cell: (row) => (
        <span className="tabular flex flex-col py-1 leading-tight">
          <span>{formatFileDate(row.trash.trashedAt, i18n.language)}</span>
          <span className="text-body-sm">{daysLeft(row) <= soonDays ? warning(row) : countdown(row)}</span>
        </span>
      ),
    },
    {
      id: "size",
      label: t("files.columns.size"),
      className: "w-[11%] tabular truncate text-right @max-[600px]:hidden",
      cell: (row) => (
        <span className="flex flex-col py-1 leading-tight">
          <span>{row.size ? formatBytes(row.size, i18n.language) : "—"}</span>
          {row.trash.itemCount > 0 && (
            <span className="text-body-sm">{t("trash.items", { count: row.trash.itemCount })}</span>
          )}
        </span>
      ),
    },
  ];

  const restore = async (targets: Row[]) => {
    try {
      const restored = await drive.restore(targets.map((row) => row.id));
      setSelected([]);
      await invalidateTrash(queryClient);
      const first = restored[0];
      if (!first) return;
      const show = () =>
        void (first.node.parentId
          ? navigate({ to: "/folders/$folderId", params: { folderId: first.node.parentId } })
          : navigate({ to: "/" }));
      if (restored.some((item) => item.movedToRoot)) {
        toast(t("toasts.restoredToRoot", { count: restored.length }), { label: t("trash.show"), onClick: show });
      } else if (restored.length === 1) {
        toast(t("toasts.restoredOne", { name: first.node.name }), { label: t("trash.show"), onClick: show });
      } else {
        toast(t("toasts.restored", { count: restored.length }));
      }
    } catch (error) {
      toast(errorMessage(t, error));
    }
  };

  const confirmed = async () => {
    if (!confirm) return;
    setBusy(true);
    try {
      if (confirm.kind === "open") {
        await restore([confirm.row]);
      } else if (confirm.kind === "delete") {
        await drive.deleteForever(confirm.rows.map((row) => row.id));
        setSelected([]);
        toast(t("toasts.deletedForever", { count: confirm.rows.length }));
      } else {
        await drive.emptyTrash();
        setSelected([]);
        toast(t("toasts.trashEmptied"));
      }
    } catch (error) {
      toast(errorMessage(t, error));
    }

    setBusy(false);
    setConfirm(null);
    await invalidateTrash(queryClient);
  };

  const act = (action: string, targets: Row[]) => {
    const first = targets[0];
    if (!first) return;
    if (action === "restore") void restore(targets);
    else if (action === "deleteForever") setConfirm({ kind: "delete", rows: targets });
    else if (action === "open") setConfirm({ kind: "open", row: first });
  };

  const menu = (): MenuAction[] => [
    { id: "restore", icon: "restore_from_trash", label: t("actions.restore") },
    { id: "deleteForever", icon: "delete_forever", label: t("actions.deleteForever"), separatorBefore: true },
  ];

  const dialog = (() => {
    if (!confirm) return null;
    switch (confirm.kind) {
      case "open":
        return {
          title: t("trash.open.title", { name: confirm.row.name }),
          text: isFolder(confirm.row) ? t("trash.open.folderText") : t("trash.open.fileText"),
          action: t("actions.restore"),
          danger: false,
        };
      case "delete": {
        const [only] = confirm.rows;
        const inside = confirm.rows.reduce((sum, row) => sum + row.trash.itemCount, 0);
        return {
          title:
            confirm.rows.length === 1 && only
              ? t("trash.delete.titleOne", { name: only.name })
              : t("trash.delete.title", { count: confirm.rows.length }),
          text: [inside > 0 && t("trash.delete.inside", { count: inside }), t("trash.cantUndo")]
            .filter(Boolean)
            .join(" "),
          action: t("actions.deleteForever"),
          danger: true,
        };
      }
      case "empty":
        return {
          title: t("trash.empty.title"),
          text: `${t("trash.empty.text", {
            count: rows.length,
            size: formatBytes(listing.data?.freeableBytes ?? 0, i18n.language),
          })} ${t("trash.cantUndo")}`,
          action: t("files.emptyTrash"),
          danger: true,
        };
    }
  })();

  return (
    <main className="relative min-w-0 flex-1 overflow-y-auto rounded-tl-lg px-4 pb-40 sm:px-6 md:pb-24">
      <div className="flex flex-col gap-4">
        <div className="flex min-h-14 items-center gap-2">
          {chosen.length > 0 ? (
            <ActionBar
              count={chosen.length}
              actions={[
                { id: "restore", icon: "restore_from_trash", label: t("actions.restore") },
                { id: "deleteForever", icon: "delete_forever", label: t("actions.deleteForever") },
              ]}
              onAction={(id) => act(id, chosen)}
              onClear={() => setSelected([])}
              className="flex-1 max-sm:fixed max-sm:inset-x-3 max-sm:bottom-23 max-sm:z-20 max-sm:h-14 max-sm:shadow-elevation-1"
            />
          ) : null}
          <h1 className={chosen.length > 0 ? "m-0 flex-1 text-headline-sm max-sm:block sm:hidden" : "m-0 flex-1 text-headline-sm"}>
            {t("nav.trash")}
          </h1>
        </div>

        {listing.isError ? (
          <EmptyState icon="error" title={errorMessage(t, listing.error)} className="mt-4" />
        ) : listing.isPending ? null : rows.length === 0 ? (
          <EmptyState
            icon="delete"
            title={t("files.empty.trashTitle")}
            text={t("files.empty.trashText", { count: keepDays })}
            className="mt-4"
          />
        ) : (
          <>
            <Banner
              tone="info"
              action={
                <Button variant="text" onClick={() => setConfirm({ kind: "empty" })}>
                  {t("files.emptyTrash")}
                </Button>
              }
            >
              {t("files.trashNote", { count: keepDays })}
            </Banner>
            <FileList
              items={rows}
              selected={selected}
              onSelectedChange={setSelected}
              onOpen={(row) => act("open", [row])}
              onTrash={(ids) => act("deleteForever", ids.map((id) => byId.get(id)).filter((row): row is Row => !!row))}
              menuActions={menu}
              onAction={(action, row) => act(action, selected.includes(row.id) ? chosen : [row])}
              meta={(row) =>
                daysLeft(row) <= soonDays ? (
                  warning(row)
                ) : (
                  t("trash.meta", {
                    folder: row.trash.path.at(-1)?.name ?? t("nav.myFiles"),
                    count: daysLeft(row),
                  })
                )
              }
              columns={columns}
            />
          </>
        )}
      </div>

      <Dialog
        open={!!confirm}
        onOpenChange={(open) => !open && !busy && setConfirm(null)}
        title={dialog?.title}
        description={dialog?.text}
        width="sm"
        footer={
          <>
            <Button variant="text" onClick={() => setConfirm(null)} disabled={busy}>
              {t("common.cancel")}
            </Button>
            <Button variant={dialog?.danger ? "danger" : "filled"} onClick={() => void confirmed()} disabled={busy}>
              {dialog?.action}
            </Button>
          </>
        }
      />
    </main>
  );
}
