import { EmptyState, type Place } from "@dokument/ui";
import { useTranslation } from "react-i18next";

const empty: Record<Extract<Place, "shared">, [title: string, text: string, icon: string]> = {
  shared: ["sharedTitle", "sharedText", "folder_shared"],
};

/** Shared with me arrives with sharing in M4 (docs/design.md §24); until then it is empty. */
export function PlacePage({ place }: { place: Extract<Place, "shared"> }) {
  const { t } = useTranslation();
  const [title, text, icon] = empty[place];
  return (
    <main className="min-w-0 flex-1 overflow-y-auto px-4 sm:px-6">
      <EmptyState icon={icon} title={t(`files.empty.${title}`)} text={t(`files.empty.${text}`)} className="mt-16" />
    </main>
  );
}
