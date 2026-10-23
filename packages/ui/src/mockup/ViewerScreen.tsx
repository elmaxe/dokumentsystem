import { useState } from "react";
import { useTranslation } from "react-i18next";
import { DetailsPanel } from "../components/DetailsPanel";
import { FileViewer, type PreviewSource } from "../components/FileViewer";
import { isFolder } from "../lib/files";
import { fileMenu } from "./actions";
import { details as detailsFor } from "./data";
import { folderPath, useMockup } from "./store";

/** The file viewer (§14): the preview with the file's siblings a swipe or arrow key away. */
export function ViewerScreen() {
  const { t } = useTranslation();
  const app = useMockup();
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
    if (next && route) app.navigate({ ...route, fileId: next.id });
  };
  const close = () => route && app.navigate(route.back);

  if (!route || !item) return null;
  const location = [t("nav.myFiles"), ...folderPath(app.nodes, item.parentId).map((n) => n.name)].join(" / ");

  // The mockup has pictures, not files: a photo's thumbnail or a document's first page stands in for the preview.
  const picture = item.kind === "image" || item.kind === "video" ? item.thumbnail : item.page;
  const preview: PreviewSource = picture ? { type: "image", src: picture } : { type: "none" };

  return (
    <FileViewer
      key={item.id}
      item={item}
      preview={preview}
      onClose={close}
      onDownload={() => app.act("download", [item.id])}
      onShare={() => app.act("share", [item.id])}
      menuActions={fileMenu(t, item).filter((a) => !["open", "share", "download", "details"].includes(a.id))}
      onAction={(id) => {
        app.act(id, [item.id]);
        if (id === "trash") close();
      }}
      onPrevious={index > 0 ? () => go(-1) : undefined}
      onNext={index < siblings.length - 1 ? () => go(1) : undefined}
      detailsOpen={showDetails}
      onDetailsChange={setShowDetails}
      details={
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
      }
    />
  );
}
