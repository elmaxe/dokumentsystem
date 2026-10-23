import { contentUrl, previewUrl, type NodeDto } from "@dokument/api";
import { EmptyState, FileViewer, previewTypeOf, sortItems, useSnackbar, type DetailsTab, type PreviewSource } from "@dokument/ui";
import { useQuery } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { download, errorMessage, folderQuery, nodeQuery, toItem } from "./drive";
import { FileDetailsPanel } from "./FileDetailsPanel";

/** Images every browser shows from the original. Others (HEIC, TIFF, SVG) wait for the worker's WebP preview. */
const webImage = /^image\/(jpeg|png|webp|gif|avif)$/;

function previewOf(node: NodeDto): PreviewSource {
  const type = previewTypeOf(node.mimeType);
  if (type === "none") return { type };
  if (type === "image" && !webImage.test(node.mimeType ?? "") && node.thumbnail === "pending") return { type: "preparing" };
  return { type, src: previewUrl(node.id, node.currentVersionId) };
}

/** A file opened from the grid or list (§14): full screen, with its siblings in the same folder a key or swipe away. */
export function ViewerPage({ fileId }: { fileId: string }) {
  const { t, i18n } = useTranslation();
  const toast = useSnackbar();
  const navigate = useNavigate();
  const details = useQuery(nodeQuery(fileId));
  const parentId = details.data?.node.parentId ?? null;
  const listing = useQuery({ ...folderQuery(parentId), enabled: details.isSuccess });
  const [panel, setPanel] = useState<{ open: boolean; tab: DetailsTab }>({ open: false, tab: "info" });

  const close = () =>
    void (parentId ? navigate({ to: "/folders/$folderId", params: { folderId: parentId } }) : navigate({ to: "/" }));

  if (details.isError) {
    return (
      <div className="fixed inset-0 z-40 flex items-center justify-center bg-surface-container-highest">
        <EmptyState icon="error" title={errorMessage(t, details.error)} className="m-4 max-w-md bg-surface-container-low" />
      </div>
    );
  }

  if (!details.data) return <div className="fixed inset-0 z-40 bg-surface-container-highest" />;
  const item = toItem(details.data.node);
  const siblings = sortItems((listing.data?.items ?? []).filter((n) => n.kind === "file").map(toItem), { key: "name", dir: "asc" }, i18n.language);
  const index = siblings.findIndex((s) => s.id === fileId);
  const go = (offset: number) => {
    const next = siblings[index + offset];
    if (next) void navigate({ to: "/files/$fileId", params: { fileId: next.id }, replace: true });
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
            className="max-lg:fixed max-lg:inset-y-0 max-lg:right-0 max-lg:z-30 max-lg:shadow-elevation-3 max-sm:w-full"
          />
        }
      />
    </div>
  );
}
