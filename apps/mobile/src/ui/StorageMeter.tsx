import type { StorageUsage } from "@dokument/api";
import type { ColorToken } from "@dokument/tokens";
import { useTranslation } from "react-i18next";
import { View } from "react-native";
import { useFormat } from "../state/format";
import { Icon } from "./Icon";
import { Text } from "./Text";

type Level = "ok" | "warning" | "critical";

const looks: Record<Level, { bar: string; text: string; icon?: { name: "warning" | "error"; color: ColorToken } }> = {
  ok: { bar: "bg-primary", text: "text-on-surface-variant" },
  warning: { bar: "bg-warning", text: "text-warning", icon: { name: "warning", color: "warning" } },
  critical: { bar: "bg-error", text: "text-error", icon: { name: "error", color: "error" } },
};

/** Storage used against the quota. Warns at 80 % and is critical at 95 %, in words too (docs/design.md §16.4). */
export function StorageMeter({ usage }: { usage: StorageUsage }) {
  const { t } = useTranslation();
  const format = useFormat();
  const { usedBytes: used, quotaBytes: quota, trashBytes: trash } = usage;
  const percent = quota ? Math.min(100, Math.round((used / quota) * 100)) : 0;
  const level: Level = percent >= 95 ? "critical" : percent >= 80 ? "warning" : "ok";
  const look = looks[level];
  const amounts =
    quota === null
      ? t("storage.usedUnlimited", { used: format.size(used) })
      : t("storage.used", { used: format.size(used), quota: format.size(quota) });
  const words = level === "ok" ? amounts : t(`storage.${level}`, { amounts });

  return (
    <View className="gap-2">
      <Text variant="title-sm" className="text-on-surface">
        {t("storage.title")}
      </Text>
      {quota !== null ? (
        <View
          accessibilityRole="progressbar"
          accessibilityLabel={t("storage.title")}
          accessibilityValue={{ min: 0, max: 100, now: percent }}
          className="h-1.5 overflow-hidden rounded-full bg-surface-container-highest"
        >
          <View style={{ width: `${percent}%` }} className={`h-full rounded-full ${look.bar}`} />
        </View>
      ) : null}
      <View className="flex-row items-start gap-1">
        {look.icon ? <Icon name={look.icon.name} size={16} color={look.icon.color} filled /> : null}
        <Text variant="body-sm" className={look.text}>
          {words}
        </Text>
      </View>
      {trash > 0 ? (
        <Text variant="body-sm" className="text-on-surface-variant">
          {t("storage.inTrash", { size: format.size(trash) })}
        </Text>
      ) : null}
    </View>
  );
}
