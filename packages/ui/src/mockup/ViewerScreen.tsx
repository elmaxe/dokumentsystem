import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { Button, IconButton } from "../components/Button";
import { DetailsPanel } from "../components/DetailsPanel";
import { EmptyState } from "../components/EmptyState";
import { FileTypeIcon } from "../components/FileTypeIcon";
import { Icon } from "../components/Icon";
import { ActionMenu } from "../components/Menu";
import { isFolder } from "../lib/files";
import { fileMenu } from "./actions";
import { details as detailsFor } from "./data";
import { folderPath, useMockup } from "./store";

/** The file viewer (§14): the preview with the file's siblings a swipe or arrow key away. */
export function ViewerScreen() {
  const { t } = useTranslation();
  const app = useMockup();
  const [page, setPage] = useState(1);
  const [zoom, setZoom] = useState(1);
  const [showDetails, setShowDetails] = useState(false);
  const route = app.route.screen === "viewer" ? app.route : null;
  const item = app.nodes.find((n) => n.id === route?.fileId);
  const siblings = item
    ? app.nodes.filter(
        (n) => n.parentId === item.parentId && !isFolder(n) && n.trashedDaysLeft === undefined && !!n.sharedWithMe === !!item.sharedWithMe,
      )
    : [];
  const index = siblings.findIndex((n) => n.id === item?.id);
  const go = (offset: number) => {
    const next = siblings[index + offset];
    if (next && route) {
      app.navigate({ ...route, fileId: next.id });
      setPage(1);
      setZoom(1);
    }
  };
  const close = () => route && app.navigate(route.back);

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.target instanceof HTMLInputElement) return;
      if (event.key === "ArrowRight") go(1);
      else if (event.key === "ArrowLeft") go(-1);
      else if (event.key === "Escape" && !document.querySelector("[role=menu],[role=dialog]")) close();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  });

  if (!route || !item) return null;
  const location = [t("nav.myFiles"), ...folderPath(app.nodes, item.parentId).map((n) => n.name)].join(" / ");
  const image = item.kind === "image" || item.kind === "video" ? item.thumbnail : item.page;

  return (
    <div className="flex h-dvh flex-col bg-surface-container-highest text-on-surface">
      <header className="flex h-16 shrink-0 items-center gap-2 bg-surface-container-low px-2 sm:px-4">
        <IconButton icon="arrow_back" label={t("viewer.close")} onClick={close} />
        <FileTypeIcon kind={item.kind} className="max-sm:hidden" />
        <h1 className="m-0 min-w-0 flex-1 truncate text-title-md">{item.name}</h1>
        <Button variant="tonal" icon="person_add" onClick={() => app.act("share", [item.id])} className="max-md:hidden">
          {t("actions.share")}
        </Button>
        <IconButton icon="person_add" label={t("actions.share")} onClick={() => app.act("share", [item.id])} className="md:hidden" />
        <IconButton icon="download" label={t("actions.download")} onClick={() => app.act("download", [item.id])} />
        <IconButton
          icon="info"
          label={t("files.showDetails")}
          selected={showDetails}
          onClick={() => setShowDetails(!showDetails)}
        />
        <ActionMenu
          actions={fileMenu(t, item).filter((a) => !["open", "share", "download", "details"].includes(a.id))}
          onAction={(id) => {
            app.act(id, [item.id]);
            if (id === "trash") close();
          }}
          trigger={<IconButton icon="more_vert" label={t("common.more")} />}
        />
      </header>
      <div className="flex min-h-0 flex-1">
        <div className="relative flex min-w-0 flex-1 flex-col">
          <div className="flex min-h-0 flex-1 items-center justify-center overflow-auto p-4 sm:p-8">
            {image ? (
              <div className="relative flex max-h-full max-w-full" style={{ transform: `scale(${zoom})` }}>
                <img
                  src={image}
                  alt={item.name}
                  className="h-auto max-h-[calc(100dvh-180px)] w-auto max-w-full rounded-sm shadow-elevation-2"
                />
                {item.kind === "video" && (
                  <span className="absolute inset-0 flex items-center justify-center">
                    <span className="flex size-18 items-center justify-center rounded-full bg-scrim text-white">
                      <Icon name="play_arrow" filled size={48} />
                    </span>
                  </span>
                )}
              </div>
            ) : (
              <EmptyState
                icon="visibility_off"
                title={t("viewer.noPreviewTitle")}
                text={t("viewer.noPreviewText")}
                action={
                  <Button icon="download" onClick={() => app.act("download", [item.id])}>
                    {t("actions.download")}
                  </Button>
                }
                className="max-w-md bg-surface-container-low"
              />
            )}
          </div>
          {item.pages !== undefined && (
            <div className="mx-auto mb-4 flex items-center gap-1 rounded-xl bg-surface-container px-2 py-1 shadow-elevation-1">
              <IconButton icon="chevron_left" label={t("viewer.previous")} size="sm" disabled={page <= 1} onClick={() => setPage(page - 1)} />
              <span className="tabular px-2 text-label-lg">{t("viewer.page", { page, total: item.pages })}</span>
              <IconButton icon="chevron_right" label={t("viewer.next")} size="sm" disabled={page >= item.pages} onClick={() => setPage(page + 1)} />
              <span className="mx-1 h-6 w-px bg-outline-variant" />
              <IconButton icon="zoom_out" label={t("viewer.zoomOut")} size="sm" disabled={zoom <= 0.5} onClick={() => setZoom(zoom - 0.25)} />
              <IconButton icon="zoom_in" label={t("viewer.zoomIn")} size="sm" disabled={zoom >= 2} onClick={() => setZoom(zoom + 0.25)} />
            </div>
          )}
          {index > 0 && (
            <IconButton
              icon="chevron_left"
              label={t("viewer.previous")}
              onClick={() => go(-1)}
              className="absolute top-1/2 left-2 -translate-y-1/2 bg-surface-container shadow-elevation-1 max-sm:hidden"
            />
          )}
          {index < siblings.length - 1 && (
            <IconButton
              icon="chevron_right"
              label={t("viewer.next")}
              onClick={() => go(1)}
              className="absolute top-1/2 right-2 -translate-y-1/2 bg-surface-container shadow-elevation-1 max-sm:hidden"
            />
          )}
        </div>
        {showDetails && (
          <DetailsPanel
            item={item}
            details={detailsFor(t, item, location)}
            tab={app.details.tab}
            onTabChange={(tab) => app.setDetails({ ...app.details, tab })}
            onClose={() => setShowDetails(false)}
            onManageAccess={() => app.act("share", [item.id])}
            versions={{ versions: app.versions }}
            className="max-lg:fixed max-lg:inset-y-0 max-lg:right-0 max-lg:z-30 max-lg:shadow-elevation-3 max-sm:w-full"
          />
        )}
      </div>
    </div>
  );
}
