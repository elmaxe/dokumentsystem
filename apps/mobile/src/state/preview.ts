import { previewUrl, thumbnailUrl, type NodeDto } from "@dokument/api";
import { previewTypeOf } from "@dokument/core";
import { baseUrl } from "./api";
import { kindOfNode } from "./queries";

/** What the file screen shows (docs/design.md §14, §15.3). URLs load with the session's header. */
export type Preview =
  | { type: "image" | "pdf" | "text" | "markdown"; uri: string }
  | { type: "video" | "audio"; uri: string; poster?: string }
  | { type: "preparing" }
  | { type: "converting"; waiting: boolean; progress?: number }
  | { type: "unplayable" }
  | { type: "none" };

/** Images the phone shows from the original. Others (HEIC on Android, TIFF, SVG) wait for the worker's WebP. */
const nativeImage = /^image\/(jpeg|png|webp|gif)$/;

/** Video and audio that play from the original before the worker has looked at them. Others wait for it. */
const nativeMedia = /^(video\/(mp4|quicktime)|audio\/(mpeg|mp4|aac|wav|x-wav|flac|x-flac))$/;

const isOffice = (node: NodeDto) => ["doc", "sheet", "slides"].includes(kindOfNode(node));

/**
 * The same choice as the web's viewer: the server's preview of the current version, which is the WebP rendition, the
 * playable copy or the PDF made of an Office document where there is one, and otherwise the original. `refused` is
 * set when the server won't make a playable copy.
 */
export function previewOf(node: NodeDto, refused: boolean): Preview {
  const uri = previewUrl(node.id, node.currentVersionId, { baseUrl: baseUrl() });
  if (node.shownAsPdf) return { type: "pdf", uri };
  const type = previewTypeOf(node.mimeType);
  if (type === "none")
    return isOffice(node) && node.thumbnail === "pending" ? { type: "preparing" } : { type };
  if (type === "image") {
    return !nativeImage.test(node.mimeType ?? "") && node.thumbnail === "pending"
      ? { type: "preparing" }
      : { type, uri };
  }
  if (type !== "video" && type !== "audio") return { type, uri };

  const poster =
    type === "video" && node.thumbnail === "ready"
      ? thumbnailUrl(node.id, node.currentVersionId, "medium", baseUrl())
      : undefined;
  const media = node.media;
  if (!media && node.thumbnail === "pending" && !nativeMedia.test(node.mimeType ?? ""))
    return { type: "preparing" };
  if (!media || media.copy === "notNeeded" || media.copy === "ready") return { type, uri, poster };
  if (media.copy === "failed" || refused) return { type: "unplayable" };
  return {
    type: "converting",
    waiting: media.copy !== "making",
    progress: media.progress ?? undefined,
  };
}
