import { EmptyState, type Place } from "@dokument/ui";
import { useTranslation } from "react-i18next";

const empty: Record<Exclude<Place, "files" | "trash">, [title: string, text: string, icon: string]> = {
  shared: ["sharedTitle", "sharedText", "folder_shared"],
  recent: ["folderTitle", "folderText", "schedule"],
  starred: ["starredTitle", "starredText", "star"],
};

/** Shared with me, Recent and Starred arrive in later milestones (docs/design.md §24); until then they are empty. */
export function PlacePage({ place }: { place: Exclude<Place, "files" | "trash"> }) {
  const { t } = useTranslation();
  const [title, text, icon] = empty[place];
  return (
    <main className="min-w-0 flex-1 overflow-y-auto px-4 sm:px-6">
      <EmptyState icon={icon} title={t(`files.empty.${title}`)} text={t(`files.empty.${text}`)} className="mt-16" />
    </main>
  );
}
