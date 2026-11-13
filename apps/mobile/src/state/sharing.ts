import type { Activity, ActivityListing, NodeAccess, Person, SharedItem, ShareRole } from "@dokument/api";
import {
  infiniteQueryOptions,
  keepPreviousData,
  queryOptions,
  useQueryClient,
  type QueryClient,
} from "@tanstack/react-query";
import { useEffect } from "react";
import { useTranslation } from "react-i18next";
import { toast } from "../ui/Snackbar";
import { api, ApiError, send, unwrap } from "./api";
import { changeError } from "./changes";
import { onShares } from "./live";

type TFunction = ReturnType<typeof useTranslation>["t"];

// Sharing with people in the app (docs/design.md §9, §15.3). The query keys match the web's.

/** The top-most things shared with the person, newest first. */
export const sharedQuery = queryOptions({
  queryKey: ["shared"],
  queryFn: (): Promise<SharedItem[]> => unwrap(api().GET("/api/v1/shared-with-me")),
});

/** Who can see a node, and what the person asking may do with it. */
export const accessQuery = (id: string) =>
  queryOptions({
    queryKey: ["access", id],
    queryFn: (): Promise<NodeAccess> => unwrap(api().GET("/api/v1/nodes/{id}/access", { params: { path: { id } } })),
  });

/** People to share with, by name or email: everyone who has signed in. */
export const peopleQuery = (q: string) =>
  queryOptions({
    queryKey: ["people", q],
    queryFn: (): Promise<Person[]> => unwrap(api().GET("/api/v1/people", { params: { query: { q: q || undefined } } })),
    placeholderData: keepPreviousData,
    staleTime: 60_000,
  });

/** What happened to a node, newest first, 30 at a time (§18). Under ["node", id] so changes to it refresh it. */
export const activityQuery = (id: string) =>
  infiniteQueryOptions({
    queryKey: ["node", id, "activity"],
    queryFn: ({ pageParam }): Promise<ActivityListing> =>
      unwrap(
        api().GET("/api/v1/nodes/{id}/activity", {
          params: { path: { id }, query: pageParam ? { before: pageParam } : {} },
        }),
      ),
    initialPageParam: "",
    getNextPageParam: (last) => (last.more ? last.entries.at(-1)?.at : undefined),
  });

export const sharing = {
  share: (id: string, userId: string, role: ShareRole) =>
    unwrap(api().POST("/api/v1/nodes/{id}/shares", { params: { path: { id } }, body: { userId, role } })),
  change: (shareId: string, role: ShareRole) =>
    unwrap(api().PUT("/api/v1/shares/{id}", { params: { path: { id: shareId } }, body: { role } })),
  remove: (shareId: string) => send(api().DELETE("/api/v1/shares/{id}", { params: { path: { id: shareId } } })),
  /** Stops seeing something shared with the person directly. */
  leave: (id: string) => send(api().POST("/api/v1/nodes/{id}/leave", { params: { path: { id } } })),
  /** Opened from Shared with me: no longer New. */
  seen: (id: string) => send(api().POST("/api/v1/nodes/{id}/seen", { params: { path: { id } } })),
};

/** Refetches what shows who can see what: access lists, Shared with me, and the listings that mark shared items. */
export async function invalidateSharing(queryClient: QueryClient) {
  await Promise.all(
    ["access", "shared", "folder", "node", "starred", "recent", "search"].map((key) =>
      queryClient.invalidateQueries({ queryKey: [key] }),
    ),
  );
}

/**
 * Says when something new is shared with the person while the app is open (§9), once the notices for it settle:
 * "Anna shared “Semester 2025” with you". Mounted once, behind sign-in.
 */
export function useSharedWithYouNotices() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();

  useEffect(() => {
    let timer: ReturnType<typeof setTimeout> | undefined;
    // What was shared before the first notice of a burst, read before the notice's own refetch replaces it.
    let before: Set<string> | undefined;
    const show = async () => {
      const was = before ?? new Set<string>();
      before = undefined;
      try {
        const after = await queryClient.fetchQuery({ ...sharedQuery, staleTime: 0 });
        const added = after.filter((item) => item.new && !was.has(item.node.id));
        const first = added[0];
        if (!first) return;
        toast(
          added.length === 1
            ? t("toasts.sharedWithYou", { who: first.node.ownerName, name: first.node.name })
            : t("toasts.sharedWithYouMany", { count: added.length }),
        );
      } catch {
        // Shared with me shows it either way; the message is only a courtesy.
      }
    };
    const stop = onShares(() => {
      before ??= new Set((queryClient.getQueryData(sharedQuery.queryKey) ?? []).map((item) => item.node.id));
      clearTimeout(timer);
      timer = setTimeout(() => void show(), 500);
    });
    return () => {
      clearTimeout(timer);
      stop();
    };
  }, [t, queryClient]);
}

/** A refusal from the share endpoints, in the person's words: the server's own sentence for a role it won't give. */
export function shareError(t: TFunction, error: unknown): string {
  const problem = error instanceof ApiError ? error.problem : undefined;
  if (problem?.code === "forbidden") return t("errors.ownerOnly");
  if (problem?.code === "invalidRequest" && problem.detail) return problem.detail;
  return changeError(t, error);
}

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

/** One event as a line of Activity, as the web words it: who, and what they did. */
export function activityText(t: TFunction, entry: Activity, me: string | undefined): string {
  const who = !entry.actor ? t("activity.system") : entry.actor.id === me ? t("activity.you") : entry.actor.name;
  const name = entry.nodeName ?? t("activity.deleted");
  const place = (p: Activity["from"]) =>
    p?.folder ? `“${p.folder.name}”` : p?.top ? t("activity.top") : t("activity.hidden");
  // Hermes has no Intl.ListFormat.
  const list = (tags: string[] | null | undefined) => (tags ?? []).map((tag) => `“${tag}”`).join(", ");
  const person = entry.person?.id === me ? t("activity.you") : (entry.person?.name ?? t("activity.someone"));
  const role = entry.role ? t(`share.roles.${entry.role}`).toLowerCase() : "";

  switch (entry.action) {
    case "node.move":
      return !entry.actor && entry.tag
        ? t("activity.filed", { tag: entry.tag, name, to: place(entry.to) })
        : t("activity.move", { who, name, from: place(entry.from), to: place(entry.to) });
    case "file.tags_change": {
      const added = entry.added?.length ?? 0;
      const removed = entry.removed?.length ?? 0;
      return added && removed
        ? t("activity.tagsChanged", { who, name })
        : added
          ? t("activity.tagsAdded", { who, name, tags: list(entry.added) })
          : t("activity.tagsRemoved", { who, name, tags: list(entry.removed) });
    }
    default:
      return t(`activity.${keys[entry.action] ?? "copy"}`, {
        who,
        name,
        from: entry.fromName,
        to: entry.toName,
        number: entry.versionNumber,
        person,
        role,
      });
  }
}
