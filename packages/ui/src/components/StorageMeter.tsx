import { formatBytes } from "@dokument/core";
import { useTranslation } from "react-i18next";
import { cn } from "../lib/cn";
import { Icon } from "./Icon";
import { Progress } from "./Progress";

export type StorageMeterProps = {
  /** Bytes. */
  used: number;
  /** Bytes. Null means no limit: the meter shows only what is used. */
  quota: number | null;
  className?: string;
};

/** Storage used against the quota. Warns at 80 % and is critical at 95 %, in words too (§16.4). */
export function StorageMeter({ used, quota, className }: StorageMeterProps) {
  const { t, i18n } = useTranslation();
  const percent = quota ? Math.min(100, Math.round((used / quota) * 100)) : 0;
  const level = percent >= 95 ? "critical" : percent >= 80 ? "warning" : "ok";
  const amounts =
    quota === null
      ? t("storage.usedUnlimited", { used: formatBytes(used, i18n.language) })
      : t("storage.used", {
          used: formatBytes(used, i18n.language),
          quota: formatBytes(quota, i18n.language),
        });
  return (
    <div className={cn("flex flex-col gap-2", className)}>
      <span className="flex items-center gap-2 text-title-sm text-on-surface">
        <Icon name="cloud" />
        {t("storage.title")}
      </span>
      {quota !== null && (
        <Progress
          value={percent}
        tone={level === "critical" ? "error" : level === "warning" ? "warning" : "primary"}
          label={t("storage.title")}
          className="h-1.5"
        />
      )}
      <span
        className={cn(
          "tabular flex items-start gap-1 text-body-sm",
          level === "critical" ? "text-error" : level === "warning" ? "text-warning" : "text-on-surface-variant",
        )}
      >
        {level !== "ok" && <Icon name={level === "critical" ? "error" : "warning"} size={16} filled />}
        {level === "critical"
          ? t("storage.critical", { amounts })
          : level === "warning"
            ? t("storage.warning", { amounts })
            : amounts}
      </span>
    </div>
  );
}
