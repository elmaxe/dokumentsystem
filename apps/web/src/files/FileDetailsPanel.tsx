import { contentUrl, type PathEntry } from "@dokument/api";
import { DetailsPanel, useSnackbar, type DetailsTab, type FileVersion } from "@dokument/ui";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { download, drive, errorMessage, invalidateFolders, pathLabel, versionsQuery, type Item } from "./drive";

type Props = {
  item: Item;
  /** The folders above the file, top first. */
  folderPath: PathEntry[];
  tab: DetailsTab;
  onTabChange: (tab: DetailsTab) => void;
  onClose: () => void;
  onUploadVersion?: () => void;
  onStarChange?: (starred: boolean) => void;
  className?: string;
};

/** The details panel for one file or folder: info, and for files the version history with its actions (§16.4). */
export function FileDetailsPanel({
  item,
  folderPath,
  tab,
  onTabChange,
  onClose,
  onUploadVersion,
  onStarChange,
  className,
}: Props) {
  const { t } = useTranslation();
  const toast = useSnackbar();
  const queryClient = useQueryClient();
  const isFile = item.node.kind === "file";
  const versions = useQuery({
    ...versionsQuery(item.id),
    enabled: isFile && tab === "versions",
    select: (list) =>
      list.map(
        (v): FileVersion & { id: string } => ({
          id: v.id,
          number: v.number,
          current: v.current,
          keep: v.keepForever,
          by: v.uploadedByName,
          at: v.uploadedAt,
          size: v.size,
          removedInDays: v.purgeAt ? Math.max(0, Math.ceil((Date.parse(v.purgeAt) - Date.now()) / 86_400_000)) : undefined,
        }),
      ),
  });

  const onVersion = async (action: string, version: FileVersion & { id: string }) => {
    try {
      if (action === "download") {
        download(contentUrl(item.id, version.id));
        toast(t("toasts.downloading", { count: 1 }));
      } else if (action === "restore") {
        await drive.restoreVersion(item.id, version.id);
        toast(t("toasts.versionRestored", { number: version.number }));
      } else if (action === "keep" || action === "unkeep") {
        await drive.keepForever(item.id, version.id, action === "keep");
        toast(t(action === "keep" ? "toasts.versionKept" : "toasts.versionUnkept", { number: version.number }));
      }
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ["versions", item.id] }),
        queryClient.invalidateQueries({ queryKey: ["node", item.id] }),
        invalidateFolders(queryClient, [item.node.parentId ?? null]),
      ]);
    } catch (error) {
      toast(errorMessage(t, error));
    }
  };

  return (
    <DetailsPanel
      item={item}
      details={{
        location: pathLabel(t("nav.myFiles"), folderPath),
        created: item.node.createdAt,
        checksum: item.node.sha256 ?? undefined,
        people: [{ name: item.owner, email: item.node.ownerId, role: "owner", you: true }],
        activity: [],
      }}
      tab={tab}
      onTabChange={onTabChange}
      onClose={onClose}
      onManageAccess={() => toast(t("common.notYet"))}
      onStarChange={onStarChange}
      versions={{
        canDelete: false,
        versions: versions.data ?? [],
        onUploadVersion,
        onAction: (action, version) => void onVersion(action, version as FileVersion & { id: string }),
      }}
      className={className}
    />
  );
}
