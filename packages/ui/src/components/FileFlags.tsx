import { useTranslation } from "react-i18next";
import type { FileItem } from "../lib/files";
import { Icon } from "./Icon";

/** The small shared and starred markers after a file's details. */
export function FileFlags({ item }: { item: FileItem }) {
  const { t } = useTranslation();
  if (!item.shared && !item.starred && !item.isNew) return null;
  return (
    <span className="inline-flex shrink-0 items-center gap-1">
      {item.isNew && (
        <span className="inline-flex h-5 items-center rounded-full bg-primary px-2 text-label-sm text-on-primary">
          {t("files.new_badge")}
        </span>
      )}
      {item.shared && <Icon name="group" size={18} label={t("files.shared")} />}
      {item.starred && <Icon name="star" size={18} filled label={t("files.starred")} />}
    </span>
  );
}
