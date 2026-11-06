import type { SortKey } from "@dokument/core";
import { useTranslation } from "react-i18next";
import { setListPrefs, useListPrefs } from "../state/prefs";
import { Icon } from "./Icon";
import { Sheet, SheetAction } from "./Sheet";

const keys: SortKey[] = ["name", "modified", "size"];

/** Sort by name, modified or size; choosing the current key again flips the direction. */
export function SortSheet({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const { sort } = useListPrefs();
  return (
    <Sheet open={open} onClose={onClose} title={t("files.sortBy")}>
      {keys.map((key) => (
        <SheetAction
          key={key}
          icon={
            sort.key === key ? (
              <Icon name={sort.dir === "asc" ? "arrow_upward" : "arrow_downward"} color="primary" />
            ) : (
              <Icon name="radio_button_unchecked" color="outline" />
            )
          }
          label={t(`files.columns.${key}`)}
          selected={sort.key === key}
          onPress={() => {
            setListPrefs({
              sort:
                sort.key === key
                  ? { key, dir: sort.dir === "asc" ? "desc" : "asc" }
                  : { key, dir: key === "name" ? "asc" : "desc" },
            });
            onClose();
          }}
        />
      ))}
    </Sheet>
  );
}
