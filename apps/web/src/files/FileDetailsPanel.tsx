import { avatarUrl, contentUrl, type PathEntry, type TextPage } from "@dokument/api";
import { DetailsPanel, TextDialog, useSnackbar, type DetailsTab, type FileVersion } from "@dokument/ui";
import { queryOptions, useInfiniteQuery, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { sessionQuery } from "../api";
import { jobs } from "../admin/jobsApi";
import {
  accessQuery,
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
import { activityEntry, activityQuery } from "./activity";
import { accessPeople, canEdit } from "./sharing";
import { MadeFrom } from "./MadeFrom";
import { FileTagsSection } from "./tags";

type Props = {
  item: Item;
  /** The folders above the file, top first. */
  folderPath: PathEntry[];
  tab: DetailsTab;
  onTabChange: (tab: DetailsTab) => void;
  onClose: () => void;
  onUploadVersion?: () => void;
  onStarChange?: (starred: boolean) => void;
  /** Opens the share dialog; offered to the owner. */
  onShare?: () => void;
  /** Asks to stop seeing it; offered to people it's shared with directly. */
  onLeave?: () => void;
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
  onShare,
  onLeave,
  className,
}: Props) {
  const { t, i18n } = useTranslation();
  const toast = useSnackbar();
  const queryClient = useQueryClient();
  const isFile = item.node.kind === "file";
  const me = useQuery(sessionQuery).data?.user.id;
  const editable = canEdit(item.node.access);
  const access = useQuery({ ...accessQuery(item.id), enabled: tab === "sharing" });
  const activity = useInfiniteQuery({ ...activityQuery(item.id), enabled: tab === "activity" });
  const sharedDirectly = access.data?.people.some((p) => p.person.id === me && !!p.shareId) ?? false;
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
          byPicture: avatarUrl(v.uploadedBy, v.uploadedByAvatar),
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
  const [textDialog, setTextDialog] = useState<{ open: boolean; page: number; editPage?: number }>({ open: false, page: 1 });
  // Up to 10 pages show together; longer files show one page at a time.
  const textPages = fileText.data ? Math.max(fileText.data.pageCount ?? 0, fileText.data.maxPage) : 0;
  const paged = textPages > 10;
  /** One page of a long file, or all of a short one. */
  const textSlice = (page: number | "all") =>
    queryOptions({
      queryKey: ["fileText", item.id, versionId, page],
      queryFn: () => (page === "all" ? fileTextPage(item.id, 1, 10) : fileTextPage(item.id, page, 1)),
    });
  const dialogText = useQuery({
    ...textSlice(paged ? textDialog.page : "all"),
    enabled: isFile && textDialog.open,
  });
  const turnTextPage = (page: number) => {
    setTextDialog((d) => ({ ...d, page }));
    if (page < textPages) void queryClient.prefetchQuery(textSlice(page + 1));
  };

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
  /** Copies every page. */
  const copyText = async () => {
    try {
      const pages: TextPage[] = [];
      let next: number | null = 1;
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
  const textData = dialogText.data ?? fileText.data;
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
        location: pathLabel(item.node.ownerId === me ? t("nav.myFiles") : t("nav.sharedWithMe"), folderPath),
        created: item.node.createdAt,
        checksum: item.node.sha256 ?? undefined,
        people: access.data
          ? accessPeople(access.data, me)
          : [{ id: item.node.ownerId, name: item.owner, email: "", role: "owner", you: item.node.ownerId === me }],
        activity: (activity.data?.pages ?? []).flatMap((page) =>
          page.entries.map((entry) => activityEntry(t, entry, me, i18n.language)),
        ),
        onMoreActivity: activity.hasNextPage ? () => void activity.fetchNextPage() : undefined,
        activityLoading: activity.isPending,
      }}
      tab={tab}
      onTabChange={onTabChange}
      onClose={onClose}
      onManageAccess={item.node.access === "owner" ? onShare : undefined}
      onLeave={sharedDirectly ? onLeave : undefined}
      onStarChange={onStarChange}
      tags={
        isFile ? (
          <>
            <FileTagsSection item={item} canEdit={editable} />
            {item.node.pdf && tab === "info" && <MadeFrom id={item.id} versionId={versionId} />}
          </>
        ) : undefined
      }
      fileDetails={fileDetails.data && { details: fileDetails.data, onSave: editable ? saveDetails : undefined }}
      fileText={
        fileText.data && {
          text: fileText.data,
          canEdit: editable,
          onShowAll: (editPage) => setTextDialog({ open: true, page: editPage ?? 1, editPage }),
        }
      }
      processing={processing.data && { steps: processing.data.steps, onRetry: admin ? (id) => void retry(id) : undefined }}
      versions={{
        canDelete: false,
        canRestore: editable,
        canKeep: item.node.access === "owner",
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
        pages={dialogText.data?.pages ?? (paged ? [] : (fileText.data?.pages ?? []))}
        paging={item.kind === "slides" ? "slides" : textData.pageCount != null || textData.totalPages > 1 ? "pages" : undefined}
        pager={paged ? { page: textDialog.page, total: textPages, onPage: turnTextPage } : undefined}
        loading={paged && dialogText.isPending}
        onCopy={() => void copyText()}
        onSavePage={savePage}
        onRevertPage={revertPage}
        editPage={textDialog.editPage}
      />
    )}
    </>
  );
}
