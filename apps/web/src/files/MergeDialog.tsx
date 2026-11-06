import { stemOf, withPdfExtension } from "@dokument/core";
import { ActionMenu, Button, Dialog, FileTypeIcon, Icon, IconButton, Switch, TextField } from "@dokument/ui";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import type { Item } from "./drive";
import { pdf, pdfProblem, usePdfTasks } from "./pdf";

/** The most PDFs one merge takes, and the most pages, as the server says (§8.4). */
const maxFiles = 100;
const maxPages = 2000;

type MoveAction = "top" | "up" | "down" | "bottom" | "remove";

/**
 * Merges the chosen PDFs into one new file (§8.4): the person puts them in order, names the new file, and may move the
 * originals to the trash. The server answers at once and the tray follows the work. Key it per use.
 */
export function MergeDialog({ items, onClose, onStarted }: { items: Item[]; onClose: () => void; onStarted: () => void }) {
  const { t } = useTranslation();
  const tasks = usePdfTasks();
  const [order, setOrder] = useState(items);
  const [name, setName] = useState(() => (items[0] ? t("pdf.merge.defaultName", { stem: stemOf(items[0].name) }) : ""));
  const [trash, setTrash] = useState(false);
  const [dragging, setDragging] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const pages = order.reduce((sum, item) => sum + (item.node.pdf?.pageCount ?? 0), 0);
  const tooMany = order.length > maxFiles || pages > maxPages;

  const move = (id: string, action: MoveAction) =>
    setOrder((list) => {
      const from = list.findIndex((item) => item.id === id);
      const item = list[from];
      if (!item) return list;
      const rest = list.filter((other) => other.id !== id);
      switch (action) {
        case "remove":
          return rest;
        case "top":
          return [item, ...rest];
        case "bottom":
          return [...rest, item];
        case "up":
          return rest.toSpliced(Math.max(0, from - 1), 0, item);
        case "down":
          return rest.toSpliced(Math.min(rest.length, from + 1), 0, item);
      }
    });

  const dropOn = (target: string) => {
    if (!dragging || dragging === target) return;
    setOrder((list) => {
      const item = list.find((other) => other.id === dragging);
      if (!item) return list;
      const rest = list.filter((other) => other.id !== dragging);
      const at = rest.findIndex((other) => other.id === target);
      // Dropped on a row further down, it goes after it; further up, before it.
      const down = list.findIndex((other) => other.id === dragging) < list.findIndex((other) => other.id === target);
      return rest.toSpliced(down ? at + 1 : at, 0, item);
    });
  };

  const submit = async () => {
    const first = order[0];
    if (!first || !name.trim() || order.length < 2 || tooMany) return;
    setBusy(true);
    try {
      const op = await pdf.merge({
        ids: order.map((item) => item.id),
        folderId: first.node.parentId ?? null,
        name: withPdfExtension(name),
        trashOriginals: trash,
      });
      tasks.started(op);
      onStarted();
      onClose();
    } catch (problem) {
      setError(pdfProblem(t, problem));
    }
    setBusy(false);
  };

  return (
    <Dialog
      open={items.length > 0}
      onOpenChange={(open) => !open && onClose()}
      title={t("pdf.merge.title")}
      description={t("pdf.merge.intro")}
      width="lg"
      footer={
        <>
          <Button variant="text" onClick={onClose}>
            {t("common.cancel")}
          </Button>
          <Button onClick={() => void submit()} disabled={busy || !name.trim() || order.length < 2 || tooMany}>
            {t("pdf.merge.submit")}
          </Button>
        </>
      }
    >
      <form
        className="flex flex-col gap-4"
        onSubmit={(event) => {
          event.preventDefault();
          void submit();
        }}
      >
        <ol className="m-0 flex max-h-[40dvh] list-none flex-col overflow-y-auto rounded-sm border border-outline-variant p-0">
          {order.map((item, index) => (
            <li
              key={item.id}
              draggable
              onDragStart={(event) => {
                setDragging(item.id);
                event.dataTransfer.effectAllowed = "move";
              }}
              onDragOver={(event) => {
                if (!dragging) return;
                event.preventDefault();
                dropOn(item.id);
              }}
              onDragEnd={() => setDragging(null)}
              onDrop={(event) => event.preventDefault()}
              className={`flex min-h-14 items-center gap-3 border-b border-outline-variant py-1 pr-1 pl-2 last:border-b-0 ${dragging === item.id ? "bg-secondary-container" : ""}`}
            >
              <Icon name="drag_indicator" className="cursor-grab text-on-surface-variant max-sm:hidden" />
              <span className="tabular w-6 text-right text-body-sm text-on-surface-variant">{index + 1}</span>
              <FileTypeIcon kind="pdf" />
              <span className="flex min-w-0 flex-1 flex-col">
                <span title={item.name} className="truncate text-body-lg">
                  {item.name}
                </span>
                <span className="tabular text-body-sm text-on-surface-variant">
                  {t("pdf.pages", { count: item.node.pdf?.pageCount ?? 0 })}
                </span>
              </span>
              <ActionMenu<MoveAction>
                actions={[
                  { id: "top", icon: "vertical_align_top", label: t("pdf.merge.moveTop"), disabled: index === 0 },
                  { id: "up", icon: "arrow_upward", label: t("pdf.merge.moveUp"), disabled: index === 0 },
                  { id: "down", icon: "arrow_downward", label: t("pdf.merge.moveDown"), disabled: index === order.length - 1 },
                  {
                    id: "bottom",
                    icon: "vertical_align_bottom",
                    label: t("pdf.merge.moveBottom"),
                    disabled: index === order.length - 1,
                  },
                  {
                    id: "remove",
                    icon: "remove_circle",
                    label: t("pdf.merge.remove"),
                    separatorBefore: true,
                    disabled: order.length <= 2,
                  },
                ]}
                onAction={(action) => move(item.id, action)}
                trigger={<IconButton icon="more_vert" label={t("pdf.merge.rowMenu", { name: item.name })} />}
              />
            </li>
          ))}
        </ol>
        <p className="tabular m-0 text-body-md text-on-surface-variant">
          {tooMany ? t("pdf.merge.tooMany", { files: maxFiles, pages: maxPages }) : t("pdf.merge.total", { count: pages })}
        </p>
        <TextField
          label={t("pdf.merge.name")}
          value={name}
          error={error ?? undefined}
          maxLength={255}
          onChange={(event) => {
            setName(event.target.value);
            setError(null);
          }}
          onFocus={(event) => {
            const dot = event.target.value.lastIndexOf(".");
            event.target.setSelectionRange(0, dot > 0 ? dot : event.target.value.length);
          }}
        />
        <label className="flex cursor-pointer items-center gap-4">
          <span className="flex-1 text-body-lg">{t("pdf.merge.trash")}</span>
          <Switch checked={trash} onCheckedChange={setTrash} />
        </label>
        <p className="m-0 flex items-start gap-2 text-body-sm text-on-surface-variant">
          <Icon name="info" size={18} />
          <span>{t("pdf.notKept")}</span>
        </p>
      </form>
    </Dialog>
  );
}
