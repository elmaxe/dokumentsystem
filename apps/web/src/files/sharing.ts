import type { NodeAccess, NodeRole } from "@dokument/api";
import { useSnackbar, type SharePerson } from "@dokument/ui";
import { useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { useEffect } from "react";
import { useTranslation } from "react-i18next";
import { sessionQuery } from "../api";
import { onShares } from "../live";
import { sharedQuery } from "./drive";

/** Refetches what shows who can see what: the access lists, Shared with me, and the shared icons on listings. */
export async function invalidateSharing(queryClient: QueryClient) {
  await Promise.all(
    ["access", "shared", "folder", "node", "starred", "recent", "search"].map((key) =>
      queryClient.invalidateQueries({ queryKey: [key] }),
    ),
  );
}

/** The owner first, then everyone with access, as the share dialog and the Sharing tab list them (§9). */
export function accessPeople(access: NodeAccess, you: string | undefined): SharePerson[] {
  return [
    {
      id: access.owner.id,
      name: access.owner.name,
      email: access.owner.email ?? "",
      role: "owner",
      you: access.owner.id === you,
      tone: 1,
    },
    ...access.people.map(
      (p): SharePerson => ({
        id: p.person.id,
        name: p.person.name,
        email: p.person.email ?? "",
        role: p.role,
        you: p.person.id === you,
        inheritedFrom: p.inheritedFrom?.name,
        inheritedRole: p.inheritedRole ?? undefined,
        removable: !!p.shareId,
      }),
    ),
  ];
}

/** Whether the person may change a node's contents: rename, move, tag, upload, trash (§9). */
export const canEdit = (access: NodeRole | undefined) => access === "owner" || access === "editor";

/**
 * Says when something new is shared with the person while the app is open (§9), once the notices for it settle.
 * Returns how many shared items they haven't opened yet, for the badge on Shared with me.
 */
export function useSharedWithYou(): number {
  const { t } = useTranslation();
  const toast = useSnackbar();
  const queryClient = useQueryClient();
  const shared = useQuery(sharedQuery);
  const fresh = shared.data?.filter((item) => item.new) ?? [];

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
  }, [t, toast, queryClient]);

  return fresh.length;
}

/** What a path starts from: My files for the person's own things, Shared with me for someone else's (§9). */
export function useRootName(): (ownerId: string) => string {
  const { t } = useTranslation();
  const me = useQuery(sessionQuery).data?.user.id;
  return (ownerId) => (me && ownerId !== me ? t("nav.sharedWithMe") : t("nav.myFiles"));
}
