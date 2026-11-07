import { captionsUrl, contentUrl, previewUrl, seekPreviewsUrl, thumbnailUrl, waveformUrl, type NodeDto } from "@dokument/api";
import {
  Banner,
  Button,
  EmptyState,
  FileViewer,
  kindOf,
  leadInMs,
  previewTypeOf,
  sortItems,
  useFormat,
  useSnackbar,
  type PreviewSource,
} from "@dokument/ui";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { sessionQuery } from "../api";
import { onRemoteFileChange } from "../live";
import {
  ApiError,
  download,
  drive,
  errorMessage,
  folderQuery,
  invalidateStars,
  nodeQuery,
  recentQuery,
  starredQuery,
  toItem,
  transcriptQuery,
  wordsQuery,
} from "./drive";
import { rememberViewed, useDetailsPanel } from "./detailsPanel";
import { FileDetailsPanel } from "./FileDetailsPanel";
import { ShareNodeDialog } from "./ShareNodeDialog";
import { canEdit } from "./sharing";
import { lastSearchResults, matchesQuery } from "../search/search";

/** Images every browser shows from the original. Others (HEIC, TIFF, SVG) wait for the worker's WebP preview. */
const webImage = /^image\/(jpeg|png|webp|gif|avif)$/;

/** Video and audio that usually play from the original, before the worker has looked at them. Others wait for it. */
const webMedia = /^(video\/(mp4|webm|ogg)|audio\/(mpeg|mp4|aac|ogg|opus|wav|x-wav|webm|flac|x-flac))$/;

/**
 * How the viewer shows a file: by its type, or, for a recording stored with a type nobody knew (an .amr uploaded
 * before the server knew the name), by what ffprobe found in it.
 */
function viewTypeOf(node: NodeDto) {
  const type = previewTypeOf(node.mimeType);
  if (type !== "none" || !node.media) return type;
  return node.media.hasVideo ? "video" : node.media.hasAudio ? "audio" : type;
}

function previewOf(node: NodeDto, versionId: string | null | undefined, refused: boolean): PreviewSource {
  // Office documents are shown as the PDF the worker had Gotenberg make of them (§14).
  if (node.shownAsPdf) return { type: "pdf", src: previewUrl(node.id, versionId) };
  const type = viewTypeOf(node);
  if (type === "none") return isOffice(node) && node.thumbnail === "pending" ? { type: "preparing" } : { type };
  if (type === "image" && !webImage.test(node.mimeType ?? "") && node.thumbnail === "pending") return { type: "preparing" };
  const src = previewUrl(node.id, versionId);
  // What the worker found applies to the current version; an older one just gets what the server has for it.
  if (type !== "video" && type !== "audio") return { type, src };
  const media = versionId === node.currentVersionId ? node.media : null;
  // An audio file without cover art has its waveform as its thumbnail, which isn't a cover for the player.
  const pictured = type === "video" || !!media?.hasCover;
  const poster = pictured && node.thumbnail === "ready" ? thumbnailUrl(node.id, versionId) : undefined;
  const waveform = type === "audio" && media?.hasWaveform ? waveformUrl(node.id, versionId) : undefined;
  if (!media && node.thumbnail === "pending" && !webMedia.test(node.mimeType ?? "")) return { type: "preparing" };
  // The frames over the timeline while seeking, once the worker has made them (§14).
  const seekPreviews = media?.seekPreviews ? { src: seekPreviewsUrl(node.id), ...media.seekPreviews } : undefined;
  if (!media || media.copy === "notNeeded") return { type, src, poster, seekPreviews, waveform };

  const original = playsOriginal(node) ? previewUrl(node.id, versionId, { original: true }) : null;
  if (media.copy === "ready")
    return original ? { type, src: original, fallbacks: [src], poster, seekPreviews, waveform } : { type, src, poster, seekPreviews, waveform };
  if (original) return { type, src: original, poster, seekPreviews, waveform };
  if (media.copy === "failed" || refused) return { type: "unplayable" };
  return { type: "converting", waiting: media.copy !== "making", progress: media.progress ?? undefined };
}

const isOffice = (node: NodeDto) => ["doc", "sheet", "slides"].includes(kindOf(node.name, node.mimeType));

/**
 * Whether this browser says it plays the original even though most don't, so it gets the full quality instead of the
 * copy (§14). Only HEVC, which Safari and Chromium on some systems decode; the copy is tried when it fails anyway.
 */
function playsOriginal(node: NodeDto): boolean {
  if (node.media?.videoCodec !== "hevc" || typeof document === "undefined") return false;
  const container = node.mimeType === "video/quicktime" ? "video/quicktime" : "video/mp4";
  return document.createElement("video").canPlayType(`${container}; codecs="hvc1"`) !== "";
}

/** The version being looked at when a change arrived from elsewhere, and when the file was last fetched before it. */
type Pin = { fileId: string; versionId: string; fetchedAt: number };

/**
 * A file opened from the grid or list (§14): full screen, with its siblings a key or swipe away. Opened from Starred
 * or Recent, the siblings are that page's files and closing goes back there; otherwise they're the file's folder.
 * Opened from search, the siblings are the results, and the hits for `q` are marked, starting on `page`, or a
 * recording plays from just before `at`, the second of the hit.
 */
export function ViewerPage({
  fileId,
  from,
  q,
  page,
  at,
}: {
  fileId: string;
  from?: "starred" | "recent" | "search";
  q?: string;
  page?: number;
  at?: number;
}) {
  const { t, i18n } = useTranslation();
  const toast = useSnackbar();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const details = useQuery(nodeQuery(fileId));
  const me = useQuery(sessionQuery).data?.user.id;
  const parentId = details.data?.node.parentId ?? null;
  // A file shared on its own, outside any folder shared with the person, is opened from Shared with me (§9).
  const sharedAlone = !!details.data && !!me && details.data.node.ownerId !== me && details.data.path.length === 0;
  const listing = useQuery({ ...folderQuery(parentId), enabled: details.isSuccess && !from && !sharedAlone });
  const starred = useQuery({ ...starredQuery, enabled: from === "starred" });
  const recent = useQuery({ ...recentQuery, enabled: from === "recent" });
  const results = from === "search" ? lastSearchResults() : null;
  const matches = useQuery({ ...matchesQuery(fileId, q ?? ""), enabled: !!q });
  const recording = !!details.data && ["video", "audio"].includes(viewTypeOf(details.data.node));
  const transcript = useQuery({ ...transcriptQuery(fileId), enabled: recording });
  // Open here when it was open in the file list, and the other way round (§15.2).
  const [panel, setPanel] = useDetailsPanel();
  const [sharing, setSharing] = useState(false);
  const format = useFormat();
  const current = details.data?.node.currentVersionId ?? null;
  // On while stepping between files, off the next time the viewer opens (§14).
  const [textShown, setTextShown] = useState(false);

  // A newer version uploaded elsewhere doesn't replace what the person is reading: the open version stays until they
  // ask for the newest (§15.4). Their own uploads and restores show at once, since no notice comes for them.
  const [pinned, setPinned] = useState<Pin | null>(null);
  // Once the file is fetched again with the same version, the change was about something else.
  const pin =
    pinned?.fileId === fileId && !(details.dataUpdatedAt > pinned.fetchedAt && current === pinned.versionId)
      ? pinned
      : null;
  const latest = useRef({ fileId, current, fetchedAt: details.dataUpdatedAt, pin });
  useEffect(() => {
    latest.current = { fileId, current, fetchedAt: details.dataUpdatedAt, pin };
  });
  useEffect(
    () =>
      onRemoteFileChange(() => {
        const { fileId, current, fetchedAt, pin } = latest.current;
        if (current && !pin) setPinned({ fileId, versionId: current, fetchedAt });
      }),
    [],
  );
  const shownVersion = pin?.versionId ?? current;
  const newer = !!pin && !!current && pin.versionId !== current;
  const gone = details.isError && details.error instanceof ApiError && details.error.status === 404;
  // Which pages have recognized words, with page 1's; served for the current version only.
  const showsWords = !!details.data && shownVersion === current && ["image", "pdf"].includes(previewOf(details.data.node, current, false).type);
  const words = useQuery({ ...wordsQuery(fileId, current, 1), enabled: showsWords });

  // For the person's Recent page only; opening a preview is never audited (§18).
  useEffect(() => {
    drive.opened(fileId).catch(() => {});
    rememberViewed(fileId);
  }, [fileId]);

  // Opening something shared with the person takes the New off it in Shared with me.
  const othersFile = !!details.data && !!me && details.data.node.ownerId !== me;
  useEffect(() => {
    if (!othersFile) return;
    drive
      .seen(fileId)
      .then(() => queryClient.invalidateQueries({ queryKey: ["shared"] }))
      .catch(() => {});
  }, [othersFile, fileId, queryClient]);

  // A video browsers can't play: ask for its copy now, which starts it or moves it ahead of background work. Refused
  // when copies are off (§14).
  const [refused, setRefused] = useState<string | null>(null);
  const copy = details.data?.node.media?.copy;
  const needsCopy = (copy === "none" || copy === "waiting") && shownVersion === current;
  useEffect(() => {
    if (!needsCopy) return;
    drive
      .requestPlayable(fileId)
      .then((node) => queryClient.setQueryData(nodeQuery(fileId).queryKey, (old) => (old ? { ...old, node } : old)))
      .catch(() => setRefused(fileId));
  }, [needsCopy, fileId, queryClient]);

  const redoTranscript = async () => {
    try {
      queryClient.setQueryData(transcriptQuery(fileId).queryKey, await drive.redoTranscript(fileId));
      toast(t("transcript.redoStarted"));
    } catch (error) {
      toast(errorMessage(t, error));
    }
  };

  const close = () =>
    void (from === "search"
      ? navigate({ to: "/search", search: results?.params ?? { q } })
      : from
        ? navigate({ to: `/${from}` })
      : sharedAlone
        ? navigate({ to: "/shared" })
      : parentId
        ? navigate({ to: "/folders/$folderId", params: { folderId: parentId } })
          : navigate({ to: "/" }));

  if (details.isError && !(gone && details.data)) {
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
      : from === "search"
        ? (results?.nodes ?? []).map(toItem)
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
    if (next) void navigate({ to: "/files/$fileId", params: { fileId: next.id }, search: { from, q }, replace: true });
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

  // Wait for the hits before showing a file opened from search, so it opens on the right page with them marked.
  if (q && matches.isPending) return <div className="fixed inset-0 z-40 bg-surface-container-highest" />;
  // Shown once there's something to say about it; a viewer with no transcript coming keeps its full width.
  const spokenState = shownVersion === current ? transcript.data?.state : undefined;
  const spoken = transcript.data && spokenState && spokenState !== "none" ? { ...transcript.data, state: spokenState } : undefined;
  const times = matches.data?.pages.flatMap((p) => p.times) ?? [];
  const firstHit = at !== undefined ? at * 1000 : times.length > 0 ? Math.min(...times) : undefined;
  const marks =
    q && matches.data
      ? {
          times,
          query: q,
          terms: matches.data.terms,
          pages: matches.data.pages,
          startPage: page ?? matches.data.pages[0]?.pageNo,
          onClear: () =>
            void navigate({ to: "/files/$fileId", params: { fileId }, search: { from }, replace: true }),
        }
      : undefined;

  return (
    <div className="fixed inset-0 z-40">
      <FileViewer
        key={`${item.id}-${shownVersion}-${q ?? ""}`}
        item={item}
        preview={previewOf(details.data.node, shownVersion, refused === fileId)}
        onClose={close}
        onDownload={() => {
          download(contentUrl(item.id, pin ? (shownVersion ?? undefined) : undefined));
          toast(t("toasts.downloading", { count: 1 }));
        }}
        banner={
          gone ? (
            <Banner
              tone="warning"
              icon="delete"
              action={
                <Button variant="text" onClick={() => void navigate({ to: "/trash" })}>
                  {t("live.openTrash")}
                </Button>
              }
            >
              {t("live.fileGone")}
            </Banner>
          ) : newer ? (
            <Banner
              icon="history"
              action={
                <Button variant="text" onClick={() => setPinned(null)}>
                  {t("live.showNewest")}
                </Button>
              }
            >
              {t("live.newerVersion", { date: format.date(item.modified, { inSentence: true }) })}
            </Banner>
          ) : details.data.node.shownAsPdf ? (
            <Banner icon="picture_as_pdf">{t("viewer.shownAsPdf")}</Banner>
          ) : undefined
        }
        menuActions={[
          ...(details.data.node.pdf?.readable && !pin
            ? [{ id: "split", icon: "call_split", label: t("pdf.split.action") }]
            : []),
          item.starred
            ? { id: "unstar", icon: "star", label: t("actions.unstar") }
            : { id: "star", icon: "star", label: t("actions.star") },
        ]}
        onAction={(id) =>
          id === "split"
            ? void navigate({ to: "/files/$fileId/split", params: { fileId }, search: { from: "viewer" } })
            : void setStar(id === "star")
        }
        marks={marks}
        recognized={
          showsWords && words.data
            ? {
                pages: words.data.pages,
                wordsOn: async (page) => (await queryClient.fetchQuery(wordsQuery(fileId, current, page))).words,
              }
            : undefined
        }
        textShown={textShown}
        onTextShownChange={setTextShown}
        transcript={spoken}
        onTranscriptRedo={canEdit(item.node.access) ? () => void redoTranscript() : undefined}
        captions={
          item.kind === "video" && transcript.data?.lines.length && shownVersion === current ? captionsUrl(item.id) : undefined
        }
        startAtMs={firstHit !== undefined && shownVersion === current ? Math.max(0, firstHit - leadInMs) : undefined}
        position={
          from === "search" && index >= 0 && siblings.length > 1
            ? t("viewer.resultPosition", { current: index + 1, total: siblings.length })
            : undefined
        }
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
            onShare={() => setSharing(true)}
            className="max-lg:fixed max-lg:inset-y-0 max-lg:right-0 max-lg:z-30 max-lg:shadow-elevation-3 max-sm:w-full"
          />
        }
      />
      <ShareNodeDialog node={sharing ? item : null} onClose={() => setSharing(false)} />
    </div>
  );
}
