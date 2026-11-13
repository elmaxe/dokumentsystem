import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Pressable } from "react-native";
import { setListPrefs, useListPrefs } from "../state/prefs";
import { sessionQuery } from "../state/queries";
import { AccountSheet } from "./AccountSheet";
import { Avatar } from "./Avatar";
import { IconButton } from "./Button";
import { SortSheet } from "./SortSheet";

/** The top bar's actions on file lists: grid or list, sort, and, on top-level places, the account. */
export function ListActions({ account }: { account?: boolean }) {
  const { t } = useTranslation();
  const prefs = useListPrefs();
  const [sorting, setSorting] = useState(false);
  return (
    <>
      <IconButton
        icon={prefs.view === "grid" ? "view_list" : "grid_view"}
        label={prefs.view === "grid" ? t("files.listView") : t("files.gridView")}
        onPress={() => setListPrefs({ view: prefs.view === "grid" ? "list" : "grid" })}
      />
      <IconButton icon="sort" label={t("files.sortBy")} onPress={() => setSorting(true)} />
      {account ? <AccountButton /> : null}
      <SortSheet open={sorting} onClose={() => setSorting(false)} />
    </>
  );
}

export function AccountButton() {
  const { t } = useTranslation();
  const session = useQuery(sessionQuery);
  const [open, setOpen] = useState(false);
  return (
    <>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={t("account.menu")}
        onPress={() => setOpen(true)}
        className="h-touch w-touch items-center justify-center rounded-full active:bg-surface-container-highest"
      >
        <Avatar
          name={session.data?.user.displayName ?? ""}
          id={session.data?.user.id}
          avatar={session.data?.user.avatar}
          tone={1}
        />
      </Pressable>
      <AccountSheet open={open} onClose={() => setOpen(false)} />
    </>
  );
}
