import { liveClientHeader, uploadNodeIdHeader, uploadsPath } from "@dokument/api";
import { sha256 } from "@noble/hashes/sha2.js";
import { bytesToHex } from "@noble/hashes/utils.js";
import type { QueryClient } from "@tanstack/react-query";
import * as Crypto from "expo-crypto";
import { Directory, File, Paths } from "expo-file-system";
import { useSyncExternalStore } from "react";
import { scanToPdf } from "../lib/scanPdf";
import { TusError, tusUpload, type TusSource } from "../lib/tus";
import { authHeaders, baseUrl, clientId } from "./api";
import { folderQuery, recentQuery, storageQuery } from "./queries";

/** An upload from the phone (docs/design.md §15.3), kept while the app runs so its progress shows in the folder. */
export type Upload = {
  id: string;
  name: string;
  folderId: string | null;
  size: number;
  sent: number;
  state: "uploading" | "done" | "failed";
  /** The server's reason for a refusal, such as "quotaExceeded"; none for a lost connection. */
  error?: string;
  nodeId?: string;
};

type Job = Upload & { file: File; hash: string; location?: string; abort?: AbortController };

const jobs = new Map<string, Job>();
let snapshot: Upload[] = [];
const listeners = new Set<() => void>();

function changed() {
  snapshot = [...jobs.values()].map(({ id, name, folderId, size, sent, state, error, nodeId }) => ({
    id,
    name,
    folderId,
    size,
    sent,
    state,
    error,
    nodeId,
  }));
  listeners.forEach((listener) => listener());
}

const subscribe = (listener: () => void) => {
  listeners.add(listener);
  return () => listeners.delete(listener);
};

/** Every upload since the app started, oldest first, until dismissed. */
export const useUploads = () => useSyncExternalStore(subscribe, () => snapshot);

const update = (id: string, patch: Partial<Job>) => {
  const job = jobs.get(id);
  if (!job) return;
  Object.assign(job, patch);
  changed();
};

/** Reads the file from the phone's storage a chunk at a time. */
const fileSource = (file: File): TusSource => ({
  size: file.size,
  read: async (offset, length) => {
    const handle = file.open();
    try {
      handle.offset = offset;
      return await handle.readBytes(length);
    } finally {
      handle.close();
    }
  },
});

async function run(id: string, queryClient: QueryClient) {
  const job = jobs.get(id);
  if (!job) return;
  const abort = new AbortController();
  update(id, { state: "uploading", error: undefined, abort });
  try {
    const headers = await tusUpload(fileSource(job.file), {
      endpoint: `${baseUrl()}${uploadsPath}`,
      headers: () => ({ ...authHeaders(), [liveClientHeader]: clientId }),
      metadata: {
        filename: job.name,
        filetype: "application/pdf",
        sha256: job.hash,
        modifiedAt: String(Date.now()),
        // A scan never asks about names: a second "Scan 2026-11-02 21.13" becomes "… (1)".
        onConflict: "keepBoth",
        ...(job.folderId ? { folderId: job.folderId } : {}),
      },
      location: job.location,
      onLocation: (location) => update(id, { location }),
      onProgress: (sent) => update(id, { sent }),
      signal: abort.signal,
    });
    update(id, { state: "done", nodeId: headers.get(uploadNodeIdHeader) ?? undefined, abort: undefined });
    job.file.delete();
    // Our own change: the live notice for it skips this app, so the lists are fetched again here.
    await Promise.all(
      [folderQuery(job.folderId).queryKey, storageQuery.queryKey, recentQuery.queryKey].map((queryKey) =>
        queryClient.invalidateQueries({ queryKey }),
      ),
    );
  } catch (error) {
    if (abort.signal.aborted) return;
    update(id, {
      state: "failed",
      error: error instanceof TusError ? (error.body.code ?? "generic") : undefined,
      abort: undefined,
    });
  }
}

/**
 * Makes a PDF of scanned pages and uploads it to a folder. The PDF waits in the app's cache until it's uploaded, so a
 * failed upload can be tried again.
 */
export async function uploadScan(
  pages: string[],
  name: string,
  folderId: string | null,
  queryClient: QueryClient,
): Promise<void> {
  const photos = await Promise.all(pages.map((uri) => new File(uri).bytes()));
  const pdf = await scanToPdf(photos, name.replace(/\.pdf$/i, ""));
  const folder = new Directory(Paths.cache, "scans");
  folder.create({ intermediates: true, idempotent: true });
  const id = Crypto.randomUUID();
  const file = new File(folder, `${id}.pdf`);
  file.write(pdf);
  // The scanner's photos aren't needed once they're in the PDF.
  pages.forEach((uri) => {
    try {
      new File(uri).delete();
    } catch {
      // The system clears its own cache later.
    }
  });
  jobs.set(id, { id, name, folderId, size: pdf.length, sent: 0, state: "uploading", file, hash: bytesToHex(sha256(pdf)) });
  changed();
  void run(id, queryClient);
}

export const retryUpload = (id: string, queryClient: QueryClient) => void run(id, queryClient);

/** Hides a finished upload, or stops one and throws its PDF away. */
export function dismissUpload(id: string) {
  const job = jobs.get(id);
  if (!job) return;
  job.abort?.abort();
  if (job.file.exists) job.file.delete();
  jobs.delete(id);
  changed();
}
