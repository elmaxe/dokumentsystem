import { formatBytes } from "@dokument/core";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { cn } from "../lib/cn";
import type { FileKind } from "../lib/files";
import { FileTypeIcon } from "./FileTypeIcon";
import { Icon } from "./Icon";
import { Progress } from "./Progress";
import { Button, IconButton } from "./Button";

export type UploadState = "hashing" | "uploading" | "paused" | "done" | "duplicate" | "failed";

export type UploadItem = {
  id: string;
  name: string;
  kind: FileKind;
  state: UploadState;
  /** Bytes. */
  size: number;
  uploaded?: number;
  secondsLeft?: number;
  /** Where it goes, for "Uploaded to Kvitton". */
  folder: string;
  /** For duplicates: where the same content already is. */
  duplicateOf?: string;
  /** For failures: what happened and what to do next. */
  error?: string;
  /** Hashed and checked, and waiting for the person to say what to do with a duplicate or a name clash. */
  checked?: boolean;
  /** False when trying again can't help, such as a name that isn't allowed. */
  retryable?: boolean;
  /** The folder upload this file belongs to (§8.1). Its files show as one row until it is expanded. */
  group?: string;
};

/** A folder upload: one row for all its files, with their progress added up. */
export type UploadGroup = {
  id: string;
  /** The top folder's name, as it was made ("Kvitton (1)" after Keep both). */
  name: string;
  /** Where the folder went, for "Uploaded to My files". */
  folder: string;
  /** Duplicates the person chose to skip. */
  skipped?: number;
  /** Still creating the folders: no files are listed yet. */
  preparing?: boolean;
  /** The folders couldn't be made, so nothing was uploaded. */
  error?: string;
};

export type TrayTaskState = "waiting" | "working" | "saving" | "done" | "failed" | "cancelled";

/** Work the server does with files, shown beside the uploads: merging and splitting PDFs (§8.4). */
export type TrayTask = {
  id: string;
  kind: "merge" | "split";
  /** The new file's name for a merge, the original's for a split. */
  name: string;
  state: TrayTaskState;
  pagesDone: number;
  pagesTotal: number;
  /** For a merge, how many files go in; for a split, how many come out. */
  files: number;
  /** How many of the new files have the same bytes as a file that was already there. */
  duplicates?: number;
  /** For failures: what happened, in words for people. */
  error?: string;
};

export type UploadTrayProps = {
  items: UploadItem[];
  groups?: UploadGroup[];
  tasks?: TrayTask[];
  collapsed?: boolean;
  onCollapsedChange?: (collapsed: boolean) => void;
  onClose?: () => void;
  onPause?: (id: string) => void;
  onResume?: (id: string) => void;
  onRetry?: (id: string) => void;
  onCancel?: (id: string) => void;
  onRemoveCopy?: (id: string) => void;
  onShowInFolder?: (id: string) => void;
  onPauseGroup?: (id: string) => void;
  onResumeGroup?: (id: string) => void;
  onRetryGroup?: (id: string) => void;
  onOpenGroup?: (id: string) => void;
  onDismissGroup?: (id: string) => void;
  onOpenTask?: (id: string) => void;
  onCancelTask?: (id: string) => void;
  onDismissTask?: (id: string) => void;
  onRemoveTaskCopies?: (id: string) => void;
  className?: string;
};

/** How many of a group's files to list when it is expanded; the rest are counted. */
const listedPerGroup = 20;

const stateIcons: Record<UploadState, string | null> = {
  hashing: "fingerprint",
  uploading: null,
  paused: "pause_circle",
  done: "check_circle",
  duplicate: "content_copy",
  failed: "error",
};

/**
 * One row per tus upload, each state with an icon and words (§16.4). Failed uploads stay until
 * they are retried or dismissed (§16.1). A folder upload is one row with its files added up; expanding it lists the
 * files that failed or are still on their way.
 */
export function UploadTray({
  items,
  groups = [],
  collapsed,
  onCollapsedChange,
  onClose,
  onPause,
  onResume,
  onRetry,
  onCancel,
  onRemoveCopy,
  onShowInFolder,
  onPauseGroup,
  onResumeGroup,
  onRetryGroup,
  onOpenGroup,
  onDismissGroup,
  tasks = [],
  onOpenTask,
  onCancelTask,
  onDismissTask,
  onRemoveTaskCopies,
  className,
}: UploadTrayProps) {
  const { t, i18n } = useTranslation();
  const [expanded, setExpanded] = useState<string[]>([]);
  const bytes = (n: number) => formatBytes(n, i18n.language);
  const count = (state: UploadState) => items.filter((item) => item.state === state).length;
  const busy = (item: UploadItem) => item.state === "hashing" || item.state === "uploading" || item.state === "paused";
  const summaries = new Map(groups.map((group) => [group.id, summarize(group, items)]));
  const uploading =
    items.filter((item) => !item.group && busy(item)).length +
    [...summaries.values()].filter((summary) => summary.active).length;
  const working = tasks.filter(taskBusy).length;
  const active = uploading + working;
  const skipped = groups.reduce((sum, group) => sum + (group.skipped ?? 0), 0);
  const secondsLeft = Math.max(
    0,
    ...items.filter((item) => !item.group).map((item) => item.secondsLeft ?? 0),
    ...[...summaries.values()].map((summary) => summary.secondsLeft),
  );
  const timeLeft = (seconds: number) =>
    seconds < 60
      ? t("upload.secondsLeft", { count: Math.max(1, Math.round(seconds)) })
      : t("upload.minutesLeft", { count: Math.round(seconds / 60) });

  const title =
    uploading > 0
      ? t("upload.uploading", { count: uploading })
      : working > 0
        ? t("upload.task.working", { count: working })
        : items.length + groups.length === 0
          ? t("upload.task.finished", { count: tasks.length })
          : t("upload.finished", { count: entries(items, groups).length + tasks.length });
  const summary = [
    count("done") > 0 && t("upload.summary.done", { count: count("done") }),
    count("duplicate") > 0 && t("upload.summary.duplicate", { count: count("duplicate") }),
    count("failed") > 0 && t("upload.summary.failed", { count: count("failed") }),
    skipped > 0 && t("upload.summary.skipped", { count: skipped }),
  ]
    .filter(Boolean)
    .join(" · ");
  const subtitle = collapsed && uploading > 0 ? timeLeft(secondsLeft) : summary;

  const status = (item: UploadItem) => {
    switch (item.state) {
      case "hashing":
        return t("upload.status.hashing");
      case "uploading":
        // Queued behind the two files that are sending.
        if (!item.uploaded && item.secondsLeft === undefined) return t("upload.status.waiting");
        return t("upload.status.uploading", {
          done: bytes(item.uploaded ?? 0),
          total: bytes(item.size),
          left: timeLeft(item.secondsLeft ?? 0),
        });
      case "paused":
        return t("upload.status.paused", { done: bytes(item.uploaded ?? 0), total: bytes(item.size) });
      case "done":
        return t("upload.status.done", { folder: item.folder });
      case "duplicate":
        return t("upload.status.duplicate", { path: item.duplicateOf });
      case "failed":
        return item.error ?? t("upload.status.failed");
    }
  };

  const groupStatus = (group: UploadGroup, summary: GroupSummary) => {
    const { total, done, sent, size } = summary;
    const status = (() => {
      switch (summary.state) {
        case "preparing":
          return t("upload.group.preparing");
        case "error":
          return group.error ?? t("upload.status.failed");
        case "checking":
          return t("upload.group.checking", { done: total - summary.unchecked, total });
        case "uploading":
          return t("upload.group.uploading", {
            done,
            total,
            sent: bytes(sent),
            size: bytes(size),
            left: timeLeft(summary.secondsLeft),
          });
        case "paused":
          return t("upload.group.paused", { done, total });
        case "partly":
          return t("upload.group.partly", { done, total, folder: group.folder });
        case "done":
          return total > 0
            ? t("upload.group.done", { count: total, folder: group.folder })
            : group.skipped
              ? `${t("upload.group.nothingNew")} · ${t("upload.summary.skipped", { count: group.skipped })}`
              : t("upload.group.empty", { folder: group.folder });
      }
    })();
    return status;
  };

  const action = (item: UploadItem) => {
    const small = { size: "sm" as const, className: "text-inherit" };
    switch (item.state) {
      case "uploading":
        return <IconButton icon="pause" label={t("upload.pause")} onClick={() => onPause?.(item.id)} {...small} />;
      case "paused":
        return <IconButton icon="play_arrow" label={t("upload.resume")} onClick={() => onResume?.(item.id)} {...small} />;
      case "failed":
        return (
          <>
            {item.retryable !== false && (
              <IconButton icon="refresh" label={t("upload.retry")} onClick={() => onRetry?.(item.id)} {...small} />
            )}
            <IconButton icon="close" label={t("upload.dismiss")} onClick={() => onCancel?.(item.id)} {...small} />
          </>
        );
      case "duplicate":
        return (
          <button
            type="button"
            onClick={() => onRemoveCopy?.(item.id)}
            className="cursor-pointer rounded-xs px-1 text-label-md whitespace-nowrap text-primary hover:underline"
          >
            {t("upload.removeCopy")}
          </button>
        );
      case "done":
        return (
          <IconButton icon="folder_open" label={t("upload.showInFolder")} onClick={() => onShowInFolder?.(item.id)} {...small} />
        );
      case "hashing":
        return <IconButton icon="close" label={t("common.cancel")} onClick={() => onCancel?.(item.id)} {...small} />;
    }
  };

  const row = (item: UploadItem, nested = false) => {
    const icon = stateIcons[item.state];
    return (
      <li
        key={item.id}
        className={cn(
          "grid grid-cols-[auto_1fr_auto] items-center gap-x-3 py-2 pr-2",
          nested ? "pl-13" : "pl-4",
          item.state === "failed" && "bg-error-container text-on-error-container",
        )}
      >
        <FileTypeIcon kind={item.kind} />
        <div className="flex min-w-0 flex-col gap-1">
          <span title={item.name} className="truncate font-medium">
            {item.name}
          </span>
          {(item.state === "uploading" || item.state === "paused") && (
            <Progress
              value={Math.round(((item.uploaded ?? 0) / Math.max(1, item.size)) * 100)}
              tone={item.state === "paused" ? "paused" : "primary"}
              label={item.name}
            />
          )}
          {item.state === "hashing" && <Progress label={t("upload.status.hashing")} />}
          <span
            className={cn(
              "tabular flex min-w-0 items-center gap-1 text-body-sm",
              item.state === "done" && "text-success",
              item.state === "duplicate" && "text-warning",
              item.state === "failed" && "text-on-error-container",
              busy(item) && "text-on-surface-variant",
            )}
          >
            {icon && <Icon name={icon} size={16} filled={item.state === "done" || item.state === "failed"} />}
            <span className="truncate">{status(item)}</span>
          </span>
        </div>
        <div className="flex items-center gap-0.5">{action(item)}</div>
      </li>
    );
  };

  const taskStatus = (task: TrayTask) => {
    switch (task.state) {
      case "waiting":
        return t("upload.task.waiting");
      case "working":
        return task.kind === "merge"
          ? t("upload.task.merging", { done: task.pagesDone, total: task.pagesTotal })
          : t("upload.task.splitting", { done: task.pagesDone, total: task.pagesTotal });
      case "saving":
        return t("upload.task.saving");
      case "done": {
        const done =
          task.kind === "merge"
            ? t("upload.task.merged", { count: task.files })
            : t("upload.task.splitDone", { count: task.files });
        return task.duplicates ? `${done} · ${t("upload.task.duplicates", { count: task.duplicates })}` : done;
      }
      case "failed":
        return task.error ?? t("upload.task.failed");
      case "cancelled":
        return t("upload.task.cancelled");
    }
  };

  const taskRow = (task: TrayTask) => {
    const small = { size: "sm" as const, className: "text-inherit" };
    const failed = task.state === "failed";
    const icon = task.state === "done" ? (task.duplicates ? "content_copy" : "check_circle") : failed ? "error" : task.state === "cancelled" ? "block" : null;
    return (
      <li
        key={task.id}
        className={cn(
          "grid grid-cols-[auto_1fr_auto] items-center gap-x-3 py-2 pr-2 pl-4",
          failed && "bg-error-container text-on-error-container",
        )}
      >
        <FileTypeIcon kind="pdf" />
        <div className="flex min-w-0 flex-col gap-1">
          <span title={task.name} className="truncate font-medium">
            {task.name}
          </span>
          {taskBusy(task) && (
            <Progress
              value={task.state === "working" ? Math.round((task.pagesDone / Math.max(1, task.pagesTotal)) * 100) : undefined}
              label={task.name}
            />
          )}
          <span
            className={cn(
              "tabular flex min-w-0 items-center gap-1 text-body-sm",
              task.state === "done" && (task.duplicates ? "text-warning" : "text-success"),
              failed && "text-on-error-container",
              (taskBusy(task) || task.state === "cancelled") && "text-on-surface-variant",
            )}
          >
            {icon && <Icon name={icon} size={16} filled={task.state === "done" || failed} />}
            <span className="truncate">{taskStatus(task)}</span>
          </span>
        </div>
        <div className="flex items-center gap-0.5">
          {taskBusy(task) && task.state !== "saving" && (
            <IconButton icon="close" label={t("common.cancel")} onClick={() => onCancelTask?.(task.id)} {...small} />
          )}
          {task.state === "done" && !!task.duplicates && (
            <button
              type="button"
              onClick={() => onRemoveTaskCopies?.(task.id)}
              className="cursor-pointer rounded-xs px-1 text-label-md whitespace-nowrap text-primary hover:underline"
            >
              {t("upload.removeCopy")}
            </button>
          )}
          {task.state === "done" && (
            <IconButton
              icon={task.kind === "merge" ? "open_in_new" : "folder_open"}
              label={t(task.kind === "merge" ? "actions.open" : "upload.showInFolder")}
              onClick={() => onOpenTask?.(task.id)}
              {...small}
            />
          )}
          {(failed || task.state === "cancelled") && (
            <IconButton icon="close" label={t("upload.dismiss")} onClick={() => onDismissTask?.(task.id)} {...small} />
          )}
        </div>
      </li>
    );
  };

  return (
    <section
      aria-label={t("upload.label")}
      className={cn(
        "w-95 max-w-full overflow-hidden rounded-md bg-surface-container text-on-surface shadow-elevation-2",
        className,
      )}
    >
      <div className="flex items-center gap-1 bg-surface-container-high py-2 pr-2 pl-4">
        <div className="min-w-0 flex-1" aria-live="polite">
          <div className="truncate text-title-sm">{title}</div>
          {subtitle && <div className="tabular truncate text-body-sm text-on-surface-variant">{subtitle}</div>}
        </div>
        <IconButton
          icon={collapsed ? "expand_less" : "expand_more"}
          label={collapsed ? t("upload.expand") : t("upload.collapse")}
          aria-expanded={!collapsed}
          size="sm"
          onClick={() => onCollapsedChange?.(!collapsed)}
        />
        {onClose && (
          <IconButton
            icon="close"
            label={t("upload.close")}
            size="sm"
            onClick={onClose}
            disabled={active > 0}
          />
        )}
      </div>
      {!collapsed && (
        <ul className="m-0 max-h-100 list-none overflow-auto p-0 py-1 max-sm:max-h-[40dvh]">
          {tasks.map(taskRow)}
          {entries(items, groups).map((entry) => {
            if (entry.kind === "item") return row(entry.item);
            const { group } = entry;
            const summary = summaries.get(group.id)!;
            const open = expanded.includes(group.id) && summary.total > 0;
            const listed = summary.children.filter((item) => item.state === "failed").concat(
              summary.children.filter(busy).sort((a, b) => order(a) - order(b)),
            );
            const waiting = Math.max(0, listed.length - listedPerGroup);
            const retryable = summary.children.some((item) => item.state === "failed" && item.retryable !== false);
            const tone =
              summary.state === "done"
                ? "text-success"
                : summary.state === "partly"
                  ? "text-warning"
                  : summary.state === "error"
                    ? "text-on-error-container"
                    : "text-on-surface-variant";
            const icon =
              summary.state === "done"
                ? "check_circle"
                : summary.state === "partly"
                  ? "warning"
                  : summary.state === "error"
                    ? "error"
                    : summary.state === "paused"
                      ? "pause_circle"
                      : null;
            const small = { size: "sm" as const, className: "text-inherit" };
            return (
              <li key={group.id}>
                <div
                  className={cn(
                    "grid grid-cols-[auto_1fr_auto] items-center gap-x-3 py-2 pr-2 pl-4",
                    summary.state === "error" && "bg-error-container text-on-error-container",
                  )}
                >
                  <FileTypeIcon kind="folder" />
                  <div className="flex min-w-0 flex-col gap-1">
                    <span title={group.name} className="truncate font-medium">
                      {group.name}
                    </span>
                    {summary.active && (
                      <Progress
                        value={summary.size > 0 && !group.preparing ? Math.round((summary.sent / summary.size) * 100) : undefined}
                        tone={summary.state === "paused" ? "paused" : "primary"}
                        label={group.name}
                      />
                    )}
                    <span className={cn("tabular flex min-w-0 items-center gap-1 text-body-sm", tone)}>
                      {icon && <Icon name={icon} size={16} filled={summary.state !== "paused"} />}
                      <span className="truncate">{groupStatus(group, summary)}</span>
                    </span>
                  </div>
                  <div className="flex items-center gap-0.5">
                    {summary.state === "uploading" || summary.state === "checking" ? (
                      <IconButton
                        icon="pause"
                        label={t("upload.group.pauseAll", { name: group.name })}
                        onClick={() => onPauseGroup?.(group.id)}
                        {...small}
                      />
                    ) : summary.state === "paused" ? (
                      <IconButton
                        icon="play_arrow"
                        label={t("upload.group.resumeAll", { name: group.name })}
                        onClick={() => onResumeGroup?.(group.id)}
                        {...small}
                      />
                    ) : summary.state === "done" || summary.state === "partly" ? (
                      <IconButton
                        icon="folder_open"
                        label={t("upload.group.openFolder", { name: group.name })}
                        onClick={() => onOpenGroup?.(group.id)}
                        {...small}
                      />
                    ) : summary.state === "error" ? (
                      <IconButton
                        icon="close"
                        label={t("upload.dismiss")}
                        onClick={() => onDismissGroup?.(group.id)}
                        {...small}
                      />
                    ) : null}
                    {summary.total > 0 && (
                      <IconButton
                        icon={open ? "expand_more" : "chevron_right"}
                        label={t(open ? "upload.group.hideFiles" : "upload.group.showFiles", { name: group.name })}
                        aria-expanded={open}
                        onClick={() =>
                          setExpanded((list) =>
                            list.includes(group.id) ? list.filter((id) => id !== group.id) : [...list, group.id],
                          )
                        }
                        {...small}
                      />
                    )}
                  </div>
                </div>
                {open && (
                  <ul className="m-0 list-none p-0">
                    {summary.failed > 0 && (
                      <li className="flex min-h-10 items-center gap-2 py-1 pr-2 pl-13 text-body-sm text-on-surface-variant">
                        <span className="flex-1">{t("upload.group.failed", { count: summary.failed })}</span>
                        {retryable && (
                          <Button variant="text" onClick={() => onRetryGroup?.(group.id)}>
                            {t("upload.group.retryFailed")}
                          </Button>
                        )}
                      </li>
                    )}
                    {listed.slice(0, listedPerGroup).map((item) => row(item, true))}
                    {waiting > 0 && (
                      <li className="py-1 pr-4 pb-2 pl-13 text-body-sm text-on-surface-variant">
                        {t("upload.group.waiting", { count: waiting })}
                      </li>
                    )}
                  </ul>
                )}
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}

const taskBusy = (task: TrayTask) => task.state === "waiting" || task.state === "working" || task.state === "saving";

type GroupState = "preparing" | "error" | "checking" | "uploading" | "paused" | "partly" | "done";

type GroupSummary = {
  state: GroupState;
  active: boolean;
  children: UploadItem[];
  total: number;
  done: number;
  failed: number;
  hashing: number;
  /** Files not yet hashed and checked. */
  unchecked: number;
  size: number;
  sent: number;
  secondsLeft: number;
};

/** A folder upload's files added up: how many are done, how many bytes are sent, and what it is doing now. */
function summarize(group: UploadGroup, items: UploadItem[]): GroupSummary {
  const children = items.filter((item) => item.group === group.id);
  const is = (state: UploadState) => children.filter((item) => item.state === state).length;
  const finished = (item: UploadItem) => item.state === "done" || item.state === "duplicate";
  const [hashing, uploading, paused, failed] = [is("hashing"), is("uploading"), is("paused"), is("failed")];
  const state: GroupState = group.preparing
    ? "preparing"
    : group.error
      ? "error"
      : uploading > 0
        ? "uploading"
        : hashing > 0
          ? "checking"
          : paused > 0
            ? "paused"
            : failed > 0
              ? "partly"
              : "done";
  return {
    state,
    active: state === "preparing" || state === "checking" || state === "uploading" || state === "paused",
    children,
    total: children.length,
    done: children.filter(finished).length,
    failed,
    hashing,
    unchecked: children.filter((item) => item.state === "hashing" && !item.checked).length,
    size: children.reduce((sum, item) => sum + item.size, 0),
    sent: children.reduce((sum, item) => sum + (finished(item) ? item.size : (item.uploaded ?? 0)), 0),
    secondsLeft: timeToSend(children),
  };
}

/** What's left of a folder at the speed its files are going now: each uploading file's speed follows from its own estimate. */
function timeToSend(children: UploadItem[]): number {
  let speed = 0;
  let left = 0;
  for (const item of children) {
    const remaining = item.size - (item.uploaded ?? 0);
    if (item.state === "uploading" && item.secondsLeft) speed += remaining / item.secondsLeft;
    if (item.state === "uploading" || item.state === "hashing" || item.state === "paused") left += remaining;
  }
  return speed > 0 ? left / speed : 0;
}

type Entry = { kind: "item"; item: UploadItem } | { kind: "group"; group: UploadGroup };

/** The tray's rows: files on their own, and each folder upload once, where its newest file is. */
function entries(items: UploadItem[], groups: UploadGroup[]): Entry[] {
  const byId = new Map(groups.map((group) => [group.id, group]));
  const seen = new Set<string>();
  const list: Entry[] = [];
  for (const item of items) {
    const group = item.group ? byId.get(item.group) : undefined;
    if (!group) list.push({ kind: "item", item });
    else if (!seen.has(group.id)) {
      seen.add(group.id);
      list.push({ kind: "group", group });
    }
  }
  // Folders still being made, or with no files at all, go first.
  return [
    ...groups.filter((group) => !seen.has(group.id)).map((group) => ({ kind: "group" as const, group })),
    ...list,
  ];
}

/** Files that are sending first, then those waiting their turn, paused ones, and those still being checked. */
const order = (item: UploadItem) =>
  item.state === "uploading" ? (item.uploaded || item.secondsLeft !== undefined ? 0 : 1) : item.state === "paused" ? 2 : 3;
