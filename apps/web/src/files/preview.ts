import { previewUrl, seekPreviewsUrl, thumbnailUrl, waveformUrl, type NodeDto } from "@dokument/api";
import { kindOf, previewTypeOf, type PreviewSource } from "@dokument/ui";

/** What the viewer needs to know about a file: a signed-in person's node, or one behind a public link (§9). */
export type PreviewNode = Pick<NodeDto, "id" | "name" | "mimeType" | "media" | "thumbnail" | "shownAsPdf"> & {
  currentVersionId?: string | null;
};

/** Where the viewer loads a file's renditions from. */
export type PreviewUrls = {
  preview: (nodeId: string, versionId?: string | null, options?: { original?: boolean }) => string;
  thumbnail: (nodeId: string, versionId?: string | null) => string;
  waveform: (nodeId: string, versionId?: string | null) => string;
  seekPreviews: (nodeId: string) => string;
};

/** The signed-in API's. */
export const driveUrls: PreviewUrls = {
  preview: (nodeId, versionId, options) => previewUrl(nodeId, versionId, options),
  thumbnail: (nodeId, versionId) => thumbnailUrl(nodeId, versionId),
  waveform: (nodeId, versionId) => waveformUrl(nodeId, versionId),
  seekPreviews: (nodeId) => seekPreviewsUrl(nodeId),
};

/** Images every browser shows from the original. Others (HEIC, TIFF, SVG) wait for the worker's WebP preview. */
const webImage = /^image\/(jpeg|png|webp|gif|avif)$/;

/** Video and audio that usually play from the original, before the worker has looked at them. Others wait for it. */
const webMedia = /^(video\/(mp4|webm|ogg)|audio\/(mpeg|mp4|aac|ogg|opus|wav|x-wav|webm|flac|x-flac))$/;

/**
 * How the viewer shows a file: by its type, or, for a recording stored with a type nobody knew (an .amr uploaded
 * before the server knew the name), by what ffprobe found in it.
 */
export function viewTypeOf(node: PreviewNode) {
  const type = previewTypeOf(node.mimeType);
  if (type !== "none" || !node.media) return type;
  return node.media.hasVideo ? "video" : node.media.hasAudio ? "audio" : type;
}

export function previewOf(
  node: PreviewNode,
  versionId: string | null | undefined,
  refused: boolean,
  urls: PreviewUrls = driveUrls,
): PreviewSource {
  // Office documents are shown as the PDF the worker had Gotenberg make of them (§14).
  if (node.shownAsPdf) return { type: "pdf", src: urls.preview(node.id, versionId) };
  const type = viewTypeOf(node);
  if (type === "none") return isOffice(node) && node.thumbnail === "pending" ? { type: "preparing" } : { type };
  if (type === "image" && !webImage.test(node.mimeType ?? "") && node.thumbnail === "pending") return { type: "preparing" };
  const src = urls.preview(node.id, versionId);
  // What the worker found applies to the current version; an older one just gets what the server has for it.
  if (type !== "video" && type !== "audio") return { type, src };
  const media = (versionId ?? null) === (node.currentVersionId ?? null) ? node.media : null;
  // An audio file without cover art has its waveform as its thumbnail, which isn't a cover for the player.
  const pictured = type === "video" || !!media?.hasCover;
  const poster = pictured && node.thumbnail === "ready" ? urls.thumbnail(node.id, versionId) : undefined;
  const waveform = type === "audio" && media?.hasWaveform ? urls.waveform(node.id, versionId) : undefined;
  if (!media && node.thumbnail === "pending" && !webMedia.test(node.mimeType ?? "")) return { type: "preparing" };
  // The frames over the timeline while seeking, once the worker has made them (§14).
  const seekPreviews = media?.seekPreviews ? { src: urls.seekPreviews(node.id), ...media.seekPreviews } : undefined;
  if (!media || media.copy === "notNeeded") return { type, src, poster, seekPreviews, waveform };

  const original = playsOriginal(node) ? urls.preview(node.id, versionId, { original: true }) : null;
  if (media.copy === "ready")
    return original ? { type, src: original, fallbacks: [src], poster, seekPreviews, waveform } : { type, src, poster, seekPreviews, waveform };
  if (original) return { type, src: original, poster, seekPreviews, waveform };
  if (media.copy === "failed" || refused) return { type: "unplayable" };
  return { type: "converting", waiting: media.copy !== "making", progress: media.progress ?? undefined };
}

const isOffice = (node: PreviewNode) => ["doc", "sheet", "slides"].includes(kindOf(node.name, node.mimeType));

/**
 * Whether this browser says it plays the original even though most don't, so it gets the full quality instead of the
 * copy (§14). Only HEVC, which Safari and Chromium on some systems decode; the copy is tried when it fails anyway.
 */
function playsOriginal(node: PreviewNode): boolean {
  if (node.media?.videoCodec !== "hevc" || typeof document === "undefined") return false;
  const container = node.mimeType === "video/quicktime" ? "video/quicktime" : "video/mp4";
  return document.createElement("video").canPlayType(`${container}; codecs="hvc1"`) !== "";
}

