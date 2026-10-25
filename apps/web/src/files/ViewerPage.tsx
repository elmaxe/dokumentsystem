import { contentUrl, previewUrl, type NodeDto } from "@dokument/api";
import { EmptyState, FileViewer, previewTypeOf, sortItems, useSnackbar, type DetailsTab, type PreviewSource } from "@dokument/ui";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { download, drive, errorMessage, folderQuery, invalidateStars, nodeQuery, recentQuery, starredQuery, toItem } from "./drive";
import { FileDetailsPanel } from "./FileDetailsPanel";

/** Images every browser shows from the original. Others (HEIC, TIFF, SVG) wait for the worker's WebP preview. */
const webImage = /^image\/(jpeg|png|webp|gif|avif)$/;

function previewOf(node: NodeDto): PreviewSource {
  const type = previewTypeOf(node.mimeType);
  if (type === "none") return { type };
  if (type === "image" && !webImage.test(node.mimeType ?? "") && node.thumbnail === "pending") return { type: "preparing" };
  return { type, src: previewUrl(node.id, node.currentVersionId) };
}

/**
 * A file opened from the grid or list (§14): full screen, with its siblings a key or swipe away. Opened from Starred
 * or Recent, the siblings are that page's files and closing goes back there; otherwise they're the file's folder.
 */
export function ViewerPage({ fileId, from }: { fileId: string; from?: "starred" | "recent" }) {
  const { t, i18n } = useTranslation();
  const toast = useSnackbar();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const details = useQuery(nodeQuery(fileId));
  const parentId = details.data?.node.parentId ?? null;
  const listing = useQuery({ ...folderQuery(parentId), enabled: details.isSuccess && !from });
  const starred = useQuery({ ...starredQuery, enabled: from === "starred" });
  const recent = useQuery({ ...recentQuery, enabled: from === "recent" });
  const [panel, setPanel] = useState<{ open: boolean; tab: DetailsTab }>({ open: false, tab: "info" });

  // For the person's Recent page only; opening a preview is never audited (§18).
  useEffect(() => {
    drive.opened(fileId).catch(() => {});
  }, [fileId]);

  const close = () =>
    void (from
      ? navigate({ to: `/${from}` })
      : parentId
        ? navigate({ to: "/folders/$folderId", params: { folderId: parentId } })
        : navigate({ to: "/" }));

  if (details.isError) {
    return (
      <div className="fixed inset-0 z-40 flex items-center justify-center bg-surface-container-highest">
        <EmptyState icon="error" title={errorMessage(t, details.error)} className="m-4 max-w-md bg-surface-container-low" />
      </div>
    );
  }

  if (!details.data) return <div className="fixed inset-0 z-40 bg-surface-container-highest" />;
  const item = toItem(details.data.node);
  const siblings =
    from === "recent"
      ? (recent.data?.items ?? []).map((r) => toItem(r.node))
      : sortItems(
          (from === "starred" ? (starred.data?.items ?? []).map((s) => s.node) : (listing.data?.items ?? []))
            .filter((n) => n.kind === "file")
            .map(toItem),
          { key: "name", dir: "asc" },
          i18n.language,
        );
  const index = siblings.findIndex((s) => s.id === fileId);
  const go = (offset: number) => {
    const next = siblings[index + offset];
    if (next) void navigate({ to: "/files/$fileId", params: { fileId: next.id }, search: { from }, replace: true });
  };
  const setStar = async (on: boolean) => {
    try {
      await drive.star([item.id], on);
      toast(t(on ? "toasts.starred" : "toasts.unstarred", { count: 1 }));
    } catch (error) {
      toast(errorMessage(t, error));
    }
    await invalidateStars(queryClient);
  };

  return (
    <div className="fixed inset-0 z-40">
      <FileViewer
        key={`${item.id}-${item.node.currentVersionId}`}
        item={item}
        preview={previewOf(details.data.node)}
        onClose={close}
        onDownload={() => {
          download(contentUrl(item.id));
          toast(t("toasts.downloading", { count: 1 }));
        }}
        menuActions={[
          item.starred
            ? { id: "unstar", icon: "star", label: t("actions.unstar") }
            : { id: "star", icon: "star", label: t("actions.star") },
        ]}
        onAction={(id) => void setStar(id === "star")}
        onPrevious={index > 0 ? () => go(-1) : undefined}
        onNext={index >= 0 && index < siblings.length - 1 ? () => go(1) : undefined}
        detailsOpen={panel.open}
        onDetailsChange={(open) => setPanel({ ...panel, open })}
        details={
          <FileDetailsPanel
            item={item}
            folderPath={details.data.path}
            tab={panel.tab}
            onTabChange={(tab) => setPanel({ open: true, tab })}
            onClose={() => setPanel({ ...panel, open: false })}
            onStarChange={(on) => void setStar(on)}
            className="max-lg:fixed max-lg:inset-y-0 max-lg:right-0 max-lg:z-30 max-lg:shadow-elevation-3 max-sm:w-full"
          />
        }
      />
    </div>
  );
}
