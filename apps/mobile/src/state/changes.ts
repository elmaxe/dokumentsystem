import type { NodeDto, NodeRole } from "@dokument/api";
import type { QueryClient } from "@tanstack/react-query";
import { router } from "expo-router";
import type { TFunction } from "i18next";
import { toast } from "../ui/Snackbar";
import { ApiError } from "./api";
import { drive, invalidateFolders, invalidateTrash } from "./queries";

// Changes to files and folders from the app (docs/design.md §15.3), each told in a snackbar, with Undo where the web
// has one. The app skips live notices about its own changes, so each refetches what it changed itself.

/** Editors and owners can rename, move and trash; viewers can't (§9). */
export const canEdit = (access: NodeRole | undefined) => access === "owner" || access === "editor";

/**
 * Whether the person can move these: with edit access, all from one owner's files, since moves stay within them (§9).
 * The picker refuses what sits at the top of something shared with the person, whose folder they can't see.
 */
export function useCanMove() {
  return (nodes: NodeDto[]) =>
    nodes.length > 0 && nodes.every((node) => canEdit(node.access) && node.ownerId === nodes[0]!.ownerId);
}

/** Opens the folder picker for these, starting where the first one is. */
export const startMove = (nodes: NodeDto[]) =>
  router.push({
    pathname: "/move",
    params: { ids: nodes.map((node) => node.id).join(","), from: nodes[0]?.parentId ?? "" },
  });

/** What went wrong, in the person's words. */
export function changeError(t: TFunction, error: unknown): string {
  const problem = error instanceof ApiError ? error.problem : undefined;
  switch (problem?.code) {
    case "nameTaken":
      return t("errors.nameTaken");
    case "notFound":
      return t("errors.notFound");
    case "quotaExceeded":
      return t("errors.quotaExceeded");
    case "forbidden":
      return t("errors.forbidden");
    case "invalidRequest":
      return problem.detail?.includes("into itself") ? t("errors.moveIntoItself") : t("errors.invalidName");
    default:
      return t("errors.generic");
  }
}

/** Makes a folder; answers an error message to show in the name sheet, or null when it's made. */
export async function createFolder(t: TFunction, queryClient: QueryClient, parentId: string | null, name: string) {
  try {
    await drive.createFolder(parentId, name);
  } catch (error) {
    return changeError(t, error);
  }
  toast(t("toasts.folderCreated"));
  await invalidateFolders(queryClient, [parentId]);
  return null;
}

/** Renames; answers an error message to show in the name sheet, or null when it's done. */
export async function rename(t: TFunction, queryClient: QueryClient, node: NodeDto, name: string) {
  if (name === node.name) return null;
  try {
    await drive.rename(node.id, name);
  } catch (error) {
    return changeError(t, error);
  }
  toast(t("toasts.renamed"));
  await invalidateFolders(queryClient, [node.parentId]);
  return null;
}

/** Moves the items one by one, as the web does, and offers to move them back. */
export async function move(t: TFunction, queryClient: QueryClient, nodes: NodeDto[], folderId: string | null) {
  const moved: NodeDto[] = [];
  try {
    for (const node of nodes) {
      await drive.move(node.id, folderId);
      moved.push(node);
    }
    toast(t("toasts.moved", { count: moved.length }), {
      label: t("common.undo"),
      onPress: () =>
        void (async () => {
          try {
            for (const node of moved) await drive.move(node.id, node.parentId);
          } catch (error) {
            toast(changeError(t, error));
          }
          await invalidateFolders(queryClient, [folderId, ...moved.map((node) => node.parentId)]);
        })(),
    });
  } catch (error) {
    toast(changeError(t, error));
  }
  await invalidateFolders(queryClient, [folderId, ...nodes.map((node) => node.parentId)]);
}

export async function trash(t: TFunction, queryClient: QueryClient, nodes: NodeDto[]) {
  const ids = nodes.map((node) => node.id);
  try {
    await drive.trash(ids);
    toast(t("toasts.trashed", { count: ids.length }), {
      label: t("common.undo"),
      onPress: () =>
        void drive
          .restore(ids)
          .catch((error: unknown) => toast(changeError(t, error)))
          .finally(() => void invalidateTrash(queryClient)),
    });
  } catch (error) {
    toast(changeError(t, error));
  }
  await invalidateTrash(queryClient);
}

export async function restore(t: TFunction, queryClient: QueryClient, nodes: NodeDto[]) {
  try {
    const restored = await drive.restore(nodes.map((node) => node.id));
    const toRoot = restored.filter((item) => item.movedToRoot).length;
    toast(
      toRoot > 0
        ? t("toasts.restoredToRoot", { count: restored.length })
        : restored.length === 1
          ? t("toasts.restoredOne", { name: restored[0]!.node.name })
          : t("toasts.restored", { count: restored.length }),
    );
  } catch (error) {
    toast(changeError(t, error));
  }
  await invalidateTrash(queryClient);
}

export async function deleteForever(t: TFunction, queryClient: QueryClient, ids: string[]) {
  try {
    await drive.deleteForever(ids);
    toast(t("toasts.deletedForever", { count: ids.length }));
  } catch (error) {
    toast(changeError(t, error));
  }
  await invalidateTrash(queryClient);
}

export async function emptyTrash(t: TFunction, queryClient: QueryClient) {
  try {
    await drive.emptyTrash();
    toast(t("toasts.trashEmptied"));
  } catch (error) {
    toast(changeError(t, error));
  }
  await invalidateTrash(queryClient);
}
