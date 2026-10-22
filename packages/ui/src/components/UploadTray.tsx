import { formatBytes } from "@dokument/core";
import { useTranslation } from "react-i18next";
import { cn } from "../lib/cn";
import type { FileKind } from "../lib/files";
import { IconButton } from "./Button";
import { FileTypeIcon } from "./FileTypeIcon";
import { Icon } from "./Icon";
import { Progress } from "./Progress";

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
};

export type UploadTrayProps = {
  items: UploadItem[];
  collapsed?: boolean;
  onCollapsedChange?: (collapsed: boolean) => void;
  onClose?: () => void;
  onPause?: (id: string) => void;
  onResume?: (id: string) => void;
  onRetry?: (id: string) => void;
  onCancel?: (id: string) => void;
  onRemoveCopy?: (id: string) => void;
  onShowInFolder?: (id: string) => void;
  className?: string;
};

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
 * they are retried or dismissed (§16.1).
 */
export function UploadTray({
  items,
  collapsed,
  onCollapsedChange,
  onClose,
  onPause,
  onResume,
  onRetry,
  onCancel,
  onRemoveCopy,
  onShowInFolder,
  className,
}: UploadTrayProps) {
  const { t, i18n } = useTranslation();
  const bytes = (n: number) => formatBytes(n, i18n.language);
  const count = (state: UploadState) => items.filter((item) => item.state === state).length;
  const active = count("hashing") + count("uploading") + count("paused");
  const secondsLeft = Math.max(0, ...items.map((item) => item.secondsLeft ?? 0));
  const timeLeft = (seconds: number) =>
    seconds < 60
      ? t("upload.secondsLeft", { count: Math.max(1, Math.round(seconds)) })
      : t("upload.minutesLeft", { count: Math.round(seconds / 60) });

  const title =
    active > 0
      ? t("upload.uploading", { count: active })
      : t("upload.finished", { count: items.length });
  const summary = [
    count("done") > 0 && t("upload.summary.done", { count: count("done") }),
    count("duplicate") > 0 && t("upload.summary.duplicate", { count: count("duplicate") }),
    count("failed") > 0 && t("upload.summary.failed", { count: count("failed") }),
  ]
    .filter(Boolean)
    .join(" · ");
  const subtitle = collapsed && active > 0 ? timeLeft(secondsLeft) : summary;

  const status = (item: UploadItem) => {
    switch (item.state) {
      case "hashing":
        return t("upload.status.hashing");
      case "uploading":
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
            <IconButton icon="refresh" label={t("upload.retry")} onClick={() => onRetry?.(item.id)} {...small} />
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
          {items.map((item) => {
            const icon = stateIcons[item.state];
            return (
              <li
                key={item.id}
                className={cn(
                  "grid grid-cols-[auto_1fr_auto] items-center gap-x-3 py-2 pr-2 pl-4",
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
                      value={Math.round(((item.uploaded ?? 0) / item.size) * 100)}
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
                      (item.state === "hashing" || item.state === "uploading" || item.state === "paused") &&
                        "text-on-surface-variant",
                    )}
                  >
                    {icon && (
                      <Icon name={icon} size={16} filled={item.state === "done" || item.state === "failed"} />
                    )}
                    <span className="truncate">{status(item)}</span>
                  </span>
                </div>
                <div className="flex items-center gap-0.5">{action(item)}</div>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}
