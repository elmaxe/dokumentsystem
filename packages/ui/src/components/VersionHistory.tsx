import { formatBytes, formatFileDate } from "@dokument/core";
import { useTranslation } from "react-i18next";
import { cn } from "../lib/cn";
import { Avatar, type AvatarTone } from "./Avatar";
import { Button, IconButton } from "./Button";
import { Icon } from "./Icon";
import { ActionMenu } from "./Menu";

export type FileVersion = {
  number: number;
  current?: boolean;
  /** "Keep forever" versions are exempt from retention (§8.2). */
  keep?: boolean;
  by: string;
  tone?: AvatarTone;
  /** ISO 8601. */
  at: string;
  size: number;
  comment?: string;
  /** Days until retention removes it. Only for versions that are neither current nor kept. */
  removedInDays?: number;
};

export type VersionAction = "download" | "restore" | "keep" | "unkeep" | "delete";

export type VersionHistoryProps = {
  versions: FileVersion[];
  onAction?: (action: VersionAction, version: FileVersion) => void;
  onUploadVersion?: () => void;
  /** Offer "Delete this version" on old versions. */
  canDelete?: boolean;
};

/**
 * The Versions tab of the details panel (§16.4). Marks the current and kept versions and warns
 * "Removed in N days" per the retention policy: 30 days or 100 versions (§8.2).
 */
export function VersionHistory({ versions, onAction, onUploadVersion, canDelete = true }: VersionHistoryProps) {
  const { t, i18n } = useTranslation();
  return (
    <div className="flex flex-col">
      <Button variant="tonal" icon="upload" className="my-2 w-full" onClick={onUploadVersion}>
        {t("versions.upload")}
      </Button>
      <ol className="m-0 flex list-none flex-col p-0">
        {versions.map((version) => (
          <li
            key={version.number}
            className="grid grid-cols-[24px_1fr_auto] items-start gap-x-3 border-b border-outline-variant py-3 last:border-b-0"
          >
            <span
              aria-hidden
              className={cn(
                "tabular flex size-6 items-center justify-center rounded-full text-[11px] font-semibold",
                version.current
                  ? "bg-primary text-on-primary"
                  : "bg-surface-container-highest text-on-surface-variant",
              )}
            >
              {version.number}
            </span>
            <div className="flex min-w-0 flex-col gap-0.5">
              <span className="flex flex-wrap items-center gap-2 font-medium text-on-surface">
                {t("versions.version", { number: version.number })}
                {version.current && (
                  <span className="rounded-xs bg-primary-container px-1.5 text-label-sm text-on-primary-container uppercase">
                    {t("versions.current")}
                  </span>
                )}
                {version.keep && (
                  <span className="inline-flex items-center gap-0.5 rounded-xs bg-secondary-container px-1.5 text-label-sm text-on-secondary-container uppercase">
                    <Icon name="keep" size={14} filled />
                    {t("versions.kept")}
                  </span>
                )}
              </span>
              <span className="tabular flex flex-wrap items-center gap-1 text-body-sm text-on-surface-variant">
                <Avatar name={version.by} tone={version.tone} size="sm" />
                {[
                  version.by,
                  formatFileDate(version.at, i18n.language),
                  formatBytes(version.size, i18n.language),
                ].join(" · ")}
              </span>
              {version.comment && (
                <span className="mt-0.5 text-body-md text-on-surface-variant">{version.comment}</span>
              )}
              {!version.current && !version.keep && version.removedInDays !== undefined && (
                <span className="flex items-center gap-1 text-body-sm text-warning">
                  <Icon name="schedule" size={16} />
                  {t("versions.removedIn", { count: version.removedInDays })}
                </span>
              )}
            </div>
            {onAction && (
              <ActionMenu
                onAction={(action) => onAction(action, version)}
                actions={[
                  { id: "download", icon: "download", label: t("versions.download") },
                  ...(version.current
                    ? []
                    : [{ id: "restore" as const, icon: "history", label: t("versions.restore") }]),
                  version.keep
                    ? { id: "unkeep" as const, icon: "keep_off", label: t("versions.unkeep") }
                    : { id: "keep" as const, icon: "keep", label: t("versions.keep") },
                  ...(version.current || !canDelete
                    ? []
                    : [
                        {
                          id: "delete" as const,
                          icon: "delete_forever",
                          label: t("versions.delete"),
                          danger: true,
                          separatorBefore: true,
                        },
                      ]),
                ]}
                trigger={
                  <IconButton
                    icon="more_vert"
                    size="sm"
                    label={t("versions.actionsFor", { number: version.number })}
                  />
                }
              />
            )}
          </li>
        ))}
      </ol>
      <p className="m-0 flex gap-2 py-3 text-body-sm text-on-surface-variant">
        <Icon name="info" size="sm" />
        {t("versions.policy")}
      </p>
    </div>
  );
}
