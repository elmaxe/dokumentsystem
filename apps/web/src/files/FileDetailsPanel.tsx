import { contentUrl, type PathEntry } from "@dokument/api";
import { DetailsPanel, TextDialog, useSnackbar, type DetailsTab, type FileVersion } from "@dokument/ui";
import { useInfiniteQuery, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { sessionQuery } from "../api";
import { jobs } from "../admin/jobsApi";
import {
  ApiError,
  download,
  drive,
  errorMessage,
  fileDetailsQuery,
  fileTextPage,
  fileTextQuery,
  invalidateFolders,
  pathLabel,
  processingQuery,
  versionsQuery,
  type Item,
} from "./drive";

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

  const versionId = item.node.currentVersionId;
  const processing = useQuery({ ...processingQuery(item.id, versionId), enabled: isFile && tab === "info" });
  const fileDetails = useQuery({ ...fileDetailsQuery(item.id, versionId), enabled: isFile && tab === "info" });
  const fileText = useQuery({ ...fileTextQuery(item.id, versionId), enabled: isFile && tab === "info" });
  const [textDialog, setTextDialog] = useState<{ open: boolean; editPage?: number }>({ open: false });
  const allText = useInfiniteQuery({
    queryKey: ["fileText", item.id, versionId, "all"],
    queryFn: ({ pageParam }) => fileTextPage(item.id, pageParam),
    initialPageParam: 1,
    getNextPageParam: (last) => last.nextFrom ?? undefined,
    enabled: isFile && textDialog.open,
  });

  const refreshInfo = () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: ["fileDetails", item.id] }),
      queryClient.invalidateQueries({ queryKey: ["fileText", item.id] }),
    ]);
  /** The message to show under a field when a save fails. */
  const saveError = (error: unknown) =>
    new Error(
      error instanceof ApiError && error.problem.code === "invalidRequest" && error.problem.detail
        ? error.problem.detail
        : errorMessage(t, error),
    );
  const saveDetails = async (edit: { title: string | null; description: string | null; taken: string | null }) => {
    try {
      await drive.setDetails(item.id, edit);
    } catch (error) {
      throw saveError(error);
    }
    await refreshInfo();
    toast(t("fileInfo.detailsSaved"));
  };
  const savePage = async (pageNo: number, text: string) => {
    try {
      await drive.setTextPage(item.id, pageNo, text);
    } catch (error) {
      throw saveError(error);
    }
    await refreshInfo();
    toast(t("fileInfo.pageSaved"));
  };
  const revertPage = async (pageNo: number) => {
    try {
      await drive.revertTextPage(item.id, pageNo);
    } catch (error) {
      throw saveError(error);
    }
    await refreshInfo();
    toast(t("fileInfo.pageReverted"));
  };
  /** Copies every page, fetching the ones not loaded yet. */
  const copyText = async () => {
    try {
      const pages = (allText.data?.pages ?? []).flatMap((p) => p.pages);
      let next = allText.data?.pages.at(-1)?.nextFrom ?? (allText.data ? null : 1);
      while (next != null) {
        const more = await fileTextPage(item.id, next, 100);
        pages.push(...more.pages);
        next = more.nextFrom ?? null;
      }
      await navigator.clipboard.writeText(pages.map((p) => p.text).join("\n\n"));
      toast(t("fileInfo.copied"));
    } catch (error) {
      toast(errorMessage(t, error));
    }
  };
  const textData = allText.data?.pages.at(-1) ?? fileText.data;
  const admin = useQuery(sessionQuery).data?.user.role === "admin";
  const retry = async (jobId: string) => {
    try {
      await jobs.retry(jobId);
      await queryClient.invalidateQueries({ queryKey: ["processing", item.id] });
      toast(t("processing.retried"));
    } catch (error) {
      toast(errorMessage(t, error));
    }
  };

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
    <>
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
      fileDetails={fileDetails.data && { details: fileDetails.data, onSave: saveDetails }}
      fileText={
        fileText.data && {
          text: fileText.data,
          canEdit: true,
          onShowAll: (editPage) => setTextDialog({ open: true, editPage }),
        }
      }
      processing={processing.data && { steps: processing.data.steps, onRetry: admin ? (id) => void retry(id) : undefined }}
      versions={{
        canDelete: false,
        versions: versions.data ?? [],
        onUploadVersion,
        onAction: (action, version) => void onVersion(action, version as FileVersion & { id: string }),
      }}
      className={className}
    />
    {textData && (
      <TextDialog
        open={textDialog.open}
        onOpenChange={(open) => setTextDialog((d) => ({ ...d, open }))}
        name={item.name}
        text={textData}
        pages={allText.data?.pages.flatMap((p) => p.pages) ?? fileText.data?.pages ?? []}
        paging={item.kind === "slides" ? "slides" : textData.pageCount != null || textData.totalPages > 1 ? "pages" : undefined}
        onLoadMore={allText.hasNextPage ? () => void allText.fetchNextPage() : undefined}
        onCopy={() => void copyText()}
        onSavePage={savePage}
        onRevertPage={revertPage}
        editPage={textDialog.editPage}
      />
    )}
    </>
  );
}
