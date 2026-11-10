import {
  liveClientHeader,
  uploadNodeIdHeader,
  uploadsPath,
  type DuplicateMatch,
  type NameConflictPolicy,
} from "@dokument/api";
import { sha256 } from "@noble/hashes/sha2.js";
import { bytesToHex } from "@noble/hashes/utils.js";
import AsyncStorage from "@react-native-async-storage/async-storage";
import * as Crypto from "expo-crypto";
import { Directory, File, Paths } from "expo-file-system";
import { useSyncExternalStore } from "react";
import { AppState } from "react-native";
import { scanToPdf } from "../lib/scanPdf";
import { TusError, tusUpload, type TusSource } from "../lib/tus";
import { api, authHeaders, baseUrl, clientId, unwrap } from "./api";
import { folderQuery, invalidateFolders } from "./queries";
import { queryClient } from "./queryClient";

/**
 * Uploads from the phone (docs/design.md §15.3): photos and files picked in a folder, files shared from another app,
 * and scans. Like the web app's queue (§8.1, §8.3), each file is hashed on the phone, the server says which ones the
 * person already has, duplicates and name clashes are asked about once for the whole batch, and the rest go over tus
 * two at a time. The queue is saved on the phone, so uploads stopped by the system pick up where they left off the
 * next time the app opens.
 */
export type UploadState = "checking" | "waiting" | "uploading" | "paused" | "done" | "skipped" | "failed";

export type Upload = {
  id: string;
  name: string;
  mimeType: string;
  folderId: string | null;
  folderName: string;
  size: number;
  sent: number;
  state: UploadState;
  /** The server's reason for a refusal, such as "quotaExceeded"; none for a lost connection. */
  error?: string;
  nodeId?: string;
  /** Where the person already has the same file, for one skipped as a duplicate. */
  duplicate?: DuplicateMatch;
};

/** A file to upload: one picked, shared to the app, or made by the scanner. */
export type PickedFile = { uri: string; name: string; mimeType?: string | null; modifiedAt?: number | null };

export type UploadFolder = { id: string | null; name: string };

type Job = Upload & {
  /** The app's own copy of the file, kept until it's uploaded. */
  uri: string;
  hash?: string;
  modifiedAt: number;
  onConflict: NameConflictPolicy;
  /** Where the server keeps the bytes sent so far. */
  location?: string;
  abort?: AbortController;
  startedAt?: number;
  startSent?: number;
  /** Paused by the system, not the person: picked up again when the app comes back. */
  system?: boolean;
  /** Past the duplicate and name checks, with the person's answers. */
  checked?: boolean;
};

export type DuplicatePrompt = {
  kind: "duplicates";
  key: number;
  ids: string[];
  total: number;
  matches: Record<string, DuplicateMatch>;
  /** Name clashes among the other files, asked about once the duplicates are settled, so it's one question. */
  clashes: string[];
};
export type ConflictPrompt = { kind: "conflict"; key: number; ids: string[] };
export type UploadPrompt = DuplicatePrompt | ConflictPrompt;

export type UploadsSnapshot = {
  items: Upload[];
  /** For the files being sent, from the speed so far. */
  secondsLeft?: number;
  /** Uploads that started again after the app was closed, until new bytes go out. */
  resumed: boolean;
  prompt?: UploadPrompt;
};

const parallel = 2;
/** Files are checked in batches, so a long list starts uploading before every file is hashed. */
const checkBatchFiles = 50;
const storageKey = "dokument.uploads";

const jobs = new Map<string, Job>();
let prompts: UploadPrompt[] = [];
let promptKey = 0;
let resumed = false;
let snapshot: UploadsSnapshot = { items: [], resumed: false };
const listeners = new Set<() => void>();

/** When the app last went to the background: uploads that fail across it were stopped by the system. */
let lastBackground = 0;
let watching = false;

function speed(): number | undefined {
  const now = Date.now();
  let rate = 0;
  for (const job of jobs.values()) {
    if (job.state !== "uploading" || !job.startedAt) continue;
    const seconds = (now - job.startedAt) / 1000;
    if (seconds > 1) rate += (job.sent - (job.startSent ?? 0)) / seconds;
  }
  return rate > 0 ? rate : undefined;
}

function changed() {
  const items = [...jobs.values()].map(
    ({ id, name, mimeType, folderId, folderName, size, sent, state, error, nodeId, duplicate }) => ({
      id,
      name,
      mimeType,
      folderId,
      folderName,
      size,
      sent,
      state,
      error,
      nodeId,
      duplicate,
    }),
  );
  const rate = speed();
  const left = items
    .filter((item) => ["waiting", "uploading"].includes(item.state))
    .reduce((sum, item) => sum + item.size - item.sent, 0);
  snapshot = { items, secondsLeft: rate ? left / rate : undefined, resumed, prompt: prompts[0] };
  listeners.forEach((listener) => listener());
  save();
}

const subscribe = (listener: () => void) => {
  listeners.add(listener);
  return () => listeners.delete(listener);
};

/** Every upload since the tray was last cleared, newest first, and the question to ask about them, if any. */
export const useUploads = () => useSyncExternalStore(subscribe, () => snapshot);

const update = (id: string, patch: Partial<Job>) => {
  const job = jobs.get(id);
  if (!job) return;
  Object.assign(job, patch);
  changed();
};

// Saving the queue -----------------------------------------------------------------------------------------------

type SavedJob = Omit<Job, "abort" | "startedAt" | "startSent">;
let saving: ReturnType<typeof setTimeout> | null = null;

/** Saves what's left to do, a moment after the last change. Finished uploads aren't kept. */
function save() {
  if (saving) return;
  saving = setTimeout(() => {
    saving = null;
    const left: SavedJob[] = [...jobs.values()]
      .filter((job) => job.state !== "done" && job.state !== "skipped")
      .map((job): SavedJob => {
        const saved: Partial<Job> = { ...job };
        delete saved.abort;
        delete saved.startedAt;
        delete saved.startSent;
        return saved as SavedJob;
      });
    void (left.length ? AsyncStorage.setItem(storageKey, JSON.stringify(left)) : AsyncStorage.removeItem(storageKey));
  }, 500);
}

const uploadsFolder = () => {
  const folder = new Directory(Paths.cache, "uploads");
  folder.create({ intermediates: true, idempotent: true });
  return folder;
};

/** Takes the file into the app's own folder, so it's still there if the app is closed before it's uploaded. */
async function keep(source: File, id: string): Promise<File> {
  const target = new File(uploadsFolder(), id);
  try {
    await source.move(target);
  } catch {
    // Some files, such as ones another app shared, can only be read.
    await source.copy(target);
  }
  return target;
}

const remove = (job: Job) => {
  try {
    const file = new File(job.uri);
    if (file.exists) file.delete();
  } catch {
    // The system clears its own cache later.
  }
};

// Checking -------------------------------------------------------------------------------------------------------

/** Names compare like the server does: NFC, case-insensitive (docs/design.md §6). */
const nameKey = (name: string) => name.trim().normalize("NFC").toLowerCase();

/** SHA-256 of the file, read natively a piece at a time; the web build used for screenshots has no digest. */
async function hashFile(file: File): Promise<string> {
  if (typeof file.digest === "function") {
    try {
      return await file.digest("SHA-256");
    } catch {
      // Falls back to reading it here.
    }
  }
  const hash = sha256.create();
  const handle = file.open();
  try {
    const size = handle.size ?? 0;
    for (let offset = 0; offset < size; ) {
      handle.offset = offset;
      const bytes = await handle.readBytes(Math.min(4 * 1024 * 1024, size - offset));
      if (bytes.length === 0) break;
      hash.update(bytes);
      offset += bytes.length;
    }
  } finally {
    handle.close();
  }
  return bytesToHex(hash.digest());
}

const ask = (prompt: Omit<DuplicatePrompt, "key"> | Omit<ConflictPrompt, "key">) => {
  prompts = [...prompts, { ...prompt, key: ++promptKey } as UploadPrompt];
  changed();
};

/** Names taken in each folder, fetched once per batch. */
async function takenNames(folderIds: (string | null)[]): Promise<Map<string, Set<string>>> {
  const taken = new Map<string, Set<string>>();
  await Promise.all(
    [...new Set(folderIds)].map(async (folderId) => {
      const listing = await queryClient
        .fetchQuery({ ...folderQuery(folderId), staleTime: 0 })
        .catch(() => undefined);
      taken.set(folderId ?? "root", new Set(listing?.items.map((node) => nameKey(node.name)) ?? []));
    }),
  );
  return taken;
}

/**
 * Hashes the files and checks them for duplicates a batch at a time; new files with free names start uploading at
 * once. Duplicates and name clashes are asked about at the end, for all the files together.
 */
async function prepare(ids: string[]) {
  const taken = await takenNames(ids.map((id) => jobs.get(id)?.folderId ?? null));
  const clashes = (id: string) => {
    const job = jobs.get(id);
    return !!job && !!taken.get(job.folderId ?? "root")?.has(nameKey(job.name));
  };
  const matches: Record<string, DuplicateMatch> = {};
  const clashing: string[] = [];
  let batch: string[] = [];

  const check = async () => {
    const checking = batch.filter((id) => jobs.has(id));
    batch = [];
    if (checking.length === 0) return;
    try {
      const found = (
        await unwrap(
          api().POST("/api/v1/uploads/check", {
            body: {
              files: checking.map((id) => {
                const job = jobs.get(id)!;
                return { name: job.name, size: job.size, sha256: job.hash! };
              }),
            },
          }),
        )
      ).files;
      checking.forEach((id, i) => {
        const match = found[i]?.duplicates[0];
        if (match) matches[id] = match;
        else if (clashes(id)) clashing.push(id);
        else update(id, { state: "waiting", checked: true });
      });
      pump();
    } catch (error) {
      checking.forEach((id) => failed(id, error));
    }
  };

  for (const id of ids) {
    const job = jobs.get(id);
    if (!job) continue;
    try {
      const file = new File(job.uri);
      job.hash = await hashFile(file);
    } catch (error) {
      failed(id, error);
      continue;
    }
    batch.push(id);
    if (batch.length >= checkBatchFiles) await check();
  }
  await check();

  // Files stopped while they were being checked are no longer asked about.
  const live = (list: string[]) => list.filter((id) => jobs.has(id));
  const duplicates = live(Object.keys(matches));
  if (duplicates.length > 0) {
    ask({ kind: "duplicates", ids: duplicates, total: ids.length, matches, clashes: live(clashing) });
  } else if (live(clashing).length > 0) ask({ kind: "conflict", ids: live(clashing) });
}

/** The duplicates question: leave them out, or add them anyway, which is instant (§8.3). */
export async function resolveDuplicates(choice: "skip" | "upload") {
  const prompt = prompts[0];
  if (prompt?.kind !== "duplicates") return;
  prompts = prompts.slice(1);
  if (choice === "skip") {
    prompt.ids.forEach((id) => update(id, { state: "skipped", duplicate: prompt.matches[id] }));
    if (prompt.clashes.length) ask({ kind: "conflict", ids: prompt.clashes });
    else changed();
    return;
  }
  changed();
  const clashing: string[] = [];
  for (const id of prompt.ids) {
    const job = jobs.get(id);
    if (!job?.hash) continue;
    update(id, { state: "uploading" });
    try {
      const node = await unwrap(
        api().POST("/api/v1/uploads/copy", {
          body: {
            folderId: job.folderId,
            name: job.name,
            sha256: job.hash,
            clientModifiedAt: new Date(job.modifiedAt).toISOString(),
            onConflict: "fail",
          },
        }),
      );
      finished(id, node.id);
    } catch (error) {
      // A copy whose name is taken is asked about with the other name clashes.
      if (code(error) === "nameTaken") {
        update(id, { state: "checking" });
        clashing.push(id);
      } else failed(id, error);
    }
  }
  const asking = [...prompt.clashes, ...clashing].filter((id) => jobs.has(id));
  if (asking.length) ask({ kind: "conflict", ids: asking });
}

/** The name clash question: a new version of the file that's there, both with a number added, or leave it out. */
export function resolveConflict(choice: NameConflictPolicy | "skip") {
  const prompt = prompts[0];
  if (prompt?.kind !== "conflict") return;
  prompts = prompts.slice(1);
  for (const id of prompt.ids) {
    const job = jobs.get(id);
    if (!job) continue;
    if (choice === "skip") {
      remove(job);
      jobs.delete(id);
    } else {
      job.onConflict = choice;
      job.state = "waiting";
      job.checked = true;
    }
  }
  changed();
  pump();
}

// Sending --------------------------------------------------------------------------------------------------------

const code = (error: unknown) =>
  error instanceof TusError ? (error.body.code ?? "generic") : (error as { problem?: { code?: string } })?.problem?.code;

/** Reads the file from the phone's storage a chunk at a time. */
const fileSource = (file: File, size: number): TusSource => ({
  size,
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

function finished(id: string, nodeId: string | undefined) {
  const job = jobs.get(id);
  if (!job) return;
  update(id, { state: "done", sent: job.size, nodeId, abort: undefined, system: false });
  remove(job);
  // Our own change: the live notice for it skips this app, so the lists are fetched again here.
  void invalidateFolders(queryClient, [job.folderId]);
  if (job.onConflict === "newVersion") void queryClient.invalidateQueries({ queryKey: ["versions"] });
}

function failed(id: string, error: unknown) {
  const job = jobs.get(id);
  if (!job) return;
  const reason = error instanceof TusError || (error as { problem?: unknown })?.problem ? (code(error) ?? "generic") : undefined;
  // Without a reason, the connection went. If the app was away meanwhile, the system stopped it: it carries on.
  const away = AppState.currentState !== "active" || lastBackground > (job.startedAt ?? Infinity);
  if (reason === undefined && job.state === "uploading" && job.startedAt !== undefined && away) {
    update(id, { state: AppState.currentState === "active" ? "waiting" : "paused", system: true, abort: undefined });
    pump();
    return;
  }
  // The name was taken after the check: asked about like the others.
  if (reason === "nameTaken" && job.onConflict === "fail") {
    update(id, { state: "checking", abort: undefined });
    ask({ kind: "conflict", ids: [id] });
    pump();
    return;
  }
  update(id, { state: "failed", error: reason, abort: undefined });
  pump();
}

/** Starts waiting uploads until two are running. */
function pump() {
  for (const job of jobs.values()) {
    if ([...jobs.values()].filter((j) => j.state === "uploading").length >= parallel) break;
    if (job.state === "waiting") void send(job.id);
  }
}

async function send(id: string) {
  const job = jobs.get(id);
  if (!job?.hash) return;
  const abort = new AbortController();
  update(id, { state: "uploading", error: undefined, abort, startedAt: Date.now(), startSent: job.sent, system: false });
  try {
    const file = new File(job.uri);
    const headers = await tusUpload(fileSource(file, job.size), {
      endpoint: `${baseUrl()}${uploadsPath}`,
      headers: () => ({ ...authHeaders(), [liveClientHeader]: clientId }),
      metadata: {
        filename: job.name,
        filetype: job.mimeType,
        sha256: job.hash,
        modifiedAt: String(job.modifiedAt),
        onConflict: job.onConflict,
        ...(job.folderId ? { folderId: job.folderId } : {}),
      },
      location: job.location,
      onLocation: (location) => update(id, { location }),
      onProgress: (sent) => {
        if (resumed) resumed = false;
        update(id, { sent });
      },
      signal: abort.signal,
    });
    finished(id, headers.get(uploadNodeIdHeader) ?? undefined);
    pump();
  } catch (error) {
    if (abort.signal.aborted) return;
    failed(id, error);
  }
}

function watchAppState() {
  if (watching) return;
  watching = true;
  AppState.addEventListener("change", (state) => {
    if (state !== "active") {
      lastBackground = Date.now();
      return;
    }
    // Back in the app: uploads the system stopped carry on.
    let any = false;
    for (const job of jobs.values()) {
      if (job.system && job.state === "paused") {
        job.state = "waiting";
        any = true;
      }
    }
    if (any) {
      changed();
      pump();
    }
  });
}

// Starting -------------------------------------------------------------------------------------------------------

/**
 * Uploads files to a folder: each is taken into the app's own folder, hashed and checked, then sent. Throws when a
 * file can't be read at all.
 */
export async function uploadFiles(files: PickedFile[], folder: UploadFolder): Promise<void> {
  watchAppState();
  const ids: string[] = [];
  for (const picked of files) {
    const id = Crypto.randomUUID();
    const file = await keep(new File(picked.uri), id);
    jobs.set(id, {
      id,
      name: picked.name,
      mimeType: picked.mimeType || "application/octet-stream",
      folderId: folder.id,
      folderName: folder.name,
      size: file.size,
      sent: 0,
      state: "checking",
      uri: file.uri,
      modifiedAt: picked.modifiedAt ?? Date.now(),
      onConflict: "fail",
    });
    ids.push(id);
  }
  changed();
  void prepare(ids);
}

/**
 * Makes a PDF of scanned pages and uploads it to a folder. The PDF waits in the app's cache until it's uploaded, so a
 * failed upload can be tried again. A scan never asks about names: a second "Scan 2026-11-02 21.13" becomes "… (1)".
 */
export async function uploadScan(pages: string[], name: string, folder: UploadFolder): Promise<void> {
  watchAppState();
  const photos = await Promise.all(pages.map((uri) => new File(uri).bytes()));
  const pdf = await scanToPdf(photos, name.replace(/\.pdf$/i, ""));
  const id = Crypto.randomUUID();
  const file = new File(uploadsFolder(), id);
  file.write(pdf);
  // The scanner's photos aren't needed once they're in the PDF.
  pages.forEach((uri) => {
    try {
      new File(uri).delete();
    } catch {
      // The system clears its own cache later.
    }
  });
  jobs.set(id, {
    id,
    name,
    mimeType: "application/pdf",
    folderId: folder.id,
    folderName: folder.name,
    size: pdf.length,
    sent: 0,
    state: "waiting",
    uri: file.uri,
    hash: bytesToHex(sha256(pdf)),
    modifiedAt: Date.now(),
    onConflict: "keepBoth",
    checked: true,
  });
  changed();
  pump();
}

/**
 * Picks up the uploads saved when the app last ran. Answers how many files were left and the folder, when they all
 * go to one, so the app can say it carried on.
 */
export async function restoreUploads(): Promise<{ count: number; folder?: string } | null> {
  watchAppState();
  let saved: SavedJob[];
  try {
    saved = JSON.parse((await AsyncStorage.getItem(storageKey)) ?? "[]") as SavedJob[];
  } catch {
    return null;
  }
  const checking: string[] = [];
  let carried = 0;
  for (const job of saved) {
    if (jobs.has(job.id)) continue;
    // The app's folders can move when it's updated, so the file is found by its id.
    let file: File;
    try {
      file = new File(uploadsFolder(), job.id);
      if (!file.exists) continue;
    } catch {
      continue;
    }
    const restored: Job = { ...job, uri: file.uri };
    if (job.state === "checking") {
      checking.push(job.id);
    } else if (job.state === "uploading" || job.state === "waiting" || (job.state === "paused" && job.system)) {
      restored.state = "waiting";
      carried++;
    }
    jobs.set(job.id, restored);
  }
  carried += checking.length;
  if (jobs.size === 0) {
    void AsyncStorage.removeItem(storageKey);
    return null;
  }
  resumed = carried > 0;
  changed();
  pump();
  if (checking.length) void prepare(checking);
  if (!carried) return null;
  const folders = new Set(saved.map((job) => job.folderName));
  return { count: carried, folder: folders.size === 1 ? [...folders][0] : undefined };
}

export function pauseUpload(id: string) {
  const job = jobs.get(id);
  if (!job) return;
  job.abort?.abort();
  update(id, { state: "paused", abort: undefined, system: false });
  pump();
}

/** Carries on a paused upload, or tries a failed one again from where its bytes got to. */
export function resumeUpload(id: string) {
  const job = jobs.get(id);
  if (!job) return;
  if (!job.hash || !job.checked) {
    update(id, { state: "checking", error: undefined });
    void prepare([id]);
    return;
  }
  // A refusal leaves no upload to carry on with.
  update(id, { state: "waiting", error: undefined, location: job.error ? undefined : job.location });
  pump();
}

/** Stops an upload and throws its copy away, or hides a finished one. */
export function dismissUpload(id: string) {
  const job = jobs.get(id);
  if (!job) return;
  job.abort?.abort();
  remove(job);
  jobs.delete(id);
  prompts = prompts
    .map((prompt) => ({ ...prompt, ids: prompt.ids.filter((other) => other !== id) }))
    .filter((prompt) => prompt.ids.length > 0);
  changed();
  pump();
}

/** Clears finished, skipped and failed uploads from the tray. */
export function clearFinished() {
  for (const job of [...jobs.values()]) {
    if (job.state === "done" || job.state === "skipped" || job.state === "failed") {
      remove(job);
      jobs.delete(job.id);
    }
  }
  resumed = false;
  changed();
}

/** On sign-out: stops everything and forgets the queue. */
export async function forgetUploads() {
  for (const job of jobs.values()) {
    job.abort?.abort();
    remove(job);
  }
  jobs.clear();
  prompts = [];
  resumed = false;
  changed();
  await AsyncStorage.removeItem(storageKey);
}
