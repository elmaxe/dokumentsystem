import { contentUrl, type NodeDto } from "@dokument/api";
import { Directory, File, Paths } from "expo-file-system";
import * as Sharing from "expo-sharing";
import { authHeaders, baseUrl } from "./api";
import { drive } from "./queries";

/**
 * Downloads a file's original into the app's cache and hands it to the system's share sheet, so another app can open
 * it (docs/design.md §15.2). The cached copy is replaced next time, so a newer version is never stale.
 */
export async function openInAnotherApp(node: NodeDto, signal?: AbortSignal) {
  const folder = new Directory(Paths.cache, "open", node.id);
  if (folder.exists) folder.delete();
  folder.create({ intermediates: true });
  const file = await File.downloadFileAsync(contentUrl(node.id, undefined, baseUrl()), new File(folder, node.name), {
    headers: authHeaders(),
    idempotent: true,
    signal,
  });
  void drive.opened(node.id).catch(() => {});
  await Sharing.shareAsync(file.uri, { mimeType: node.mimeType ?? undefined, dialogTitle: node.name });
}
