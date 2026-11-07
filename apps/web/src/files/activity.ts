import type { Activity, ActivityListing } from "@dokument/api";
import type { ActivityEntry } from "@dokument/ui";
import { infiniteQueryOptions } from "@tanstack/react-query";
import type { useTranslation } from "react-i18next";
import { api } from "../api";
import { unwrap } from "./drive";

type TFunction = ReturnType<typeof useTranslation>["t"];

/** What happened to a node, newest first, 30 at a time (§18). Under ["node", id] so changes to it refresh it. */
export const activityQuery = (id: string) =>
  infiniteQueryOptions({
    queryKey: ["node", id, "activity"],
    queryFn: ({ pageParam }): Promise<ActivityListing> =>
      unwrap(
        api.GET("/api/v1/nodes/{id}/activity", {
          params: { path: { id }, query: pageParam ? { before: pageParam } : {} },
        }),
      ),
    initialPageParam: "",
    getNextPageParam: (last) => (last.more ? last.entries.at(-1)?.at : undefined),
  });

const keys: Record<string, string> = {
  "folder.create": "folderCreate",
  "file.upload": "upload",
  "file.copy": "copy",
  "file.pdf_merge": "pdfMerge",
  "file.pdf_split": "pdfSplit",
  "version.upload": "versionUpload",
  "version.restore": "versionRestore",
  "version.keep_forever": "keepForever",
  "node.rename": "rename",
  "file.details_edit": "detailsEdit",
  "file.text_edit": "textEdit",
  "file.download": "download",
  "node.trash": "trash",
  "node.restore": "restore",
  "share.grant": "shareGrant",
  "share.change": "shareChange",
  "share.revoke": "shareRevoke",
  "share.leave": "shareLeave",
};

/** One audit event as a line in the Activity tab: who, and what they did, in the person's language. */
export function activityEntry(t: TFunction, entry: Activity, me: string | undefined, language: string): ActivityEntry {
  const who = !entry.actor ? t("activity.system") : entry.actor.id === me ? t("activity.you") : entry.actor.name;
  const name = entry.nodeName ?? t("activity.deleted");
  const place = (p: Activity["from"]) =>
    p?.folder ? `“${p.folder.name}”` : p?.top ? t("activity.top") : t("activity.hidden");
  const list = (tags: string[] | null | undefined) =>
    new Intl.ListFormat(language, { type: "conjunction" }).format((tags ?? []).map((tag) => `“${tag}”`));
  const person = entry.person?.id === me ? t("activity.you") : (entry.person?.name ?? t("activity.someone"));
  const role = entry.role ? t(`share.roles.${entry.role}`).toLowerCase() : "";

  let what: string;
  switch (entry.action) {
    case "node.move":
      what =
        !entry.actor && entry.tag
          ? t("activity.filed", { tag: entry.tag, name, to: place(entry.to) })
          : t("activity.move", { who, name, from: place(entry.from), to: place(entry.to) });
      break;
    case "file.tags_change": {
      const added = entry.added?.length ?? 0;
      const removed = entry.removed?.length ?? 0;
      what =
        added && removed
          ? t("activity.tagsChanged", { who, name })
          : added
            ? t("activity.tagsAdded", { who, name, tags: list(entry.added) })
            : t("activity.tagsRemoved", { who, name, tags: list(entry.removed) });
      break;
    }
    default:
      what = t(`activity.${keys[entry.action] ?? "copy"}`, {
        who,
        name,
        from: entry.fromName,
        to: entry.toName,
        number: entry.versionNumber,
        person,
        role,
      });
  }
  return { id: entry.id, who: entry.actor?.name ?? who, what, at: entry.at };
}
