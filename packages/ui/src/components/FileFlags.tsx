import { useTranslation } from "react-i18next";
import type { FileItem } from "../lib/files";
import { Icon } from "./Icon";

/** The small shared and starred markers after a file's details. */
export function FileFlags({ item }: { item: FileItem }) {
  const { t } = useTranslation();
  if (!item.shared && !item.starred) return null;
  return (
    <span className="inline-flex shrink-0 gap-1">
      {item.shared && <Icon name="group" size={18} label={t("files.shared")} />}
      {item.starred && <Icon name="star" size={18} filled label={t("files.starred")} />}
    </span>
  );
}
