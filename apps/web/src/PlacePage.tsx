import { EmptyState, type Place } from "@dokument/ui";
import { useTranslation } from "react-i18next";

const empty: Record<Exclude<Place, "files">, [title: string, text: string, icon: string]> = {
  shared: ["sharedTitle", "sharedText", "folder_shared"],
  recent: ["folderTitle", "folderText", "schedule"],
  starred: ["starredTitle", "starredText", "star"],
  trash: ["trashTitle", "trashText", "delete"],
};

/** Shared with me, Recent, Starred and Trash arrive in later milestones (docs/design.md §24); until then they are empty. */
export function PlacePage({ place }: { place: Exclude<Place, "files"> }) {
  const { t } = useTranslation();
  const [title, text, icon] = empty[place];
  return (
    <main className="min-w-0 flex-1 overflow-y-auto px-4 sm:px-6">
      <EmptyState icon={icon} title={t(`files.empty.${title}`)} text={t(`files.empty.${text}`)} className="mt-16" />
    </main>
  );
}
