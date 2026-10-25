import {
  csrfHeader,
  uploadNodeIdHeader,
  uploadsPath,
  type DuplicateMatch,
  type FolderConflictPolicy,
  type NameConflictPolicy,
} from "@dokument/api";
import { formatBytes } from "@dokument/core";
import {
  Button,
  Dialog,
  DuplicateDialog,
  kindOf,
  useSnackbar,
  type UploadGroup,
  type UploadItem,
} from "@dokument/ui";
import { useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useRef, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import * as tus from "tus-js-client";
import { getCsrfToken } from "../api";
import { ApiError, drive, errorMessage, invalidateFolders, nameKey, pathLabel, storageQuery } from "./drive";
import { validName, type PickedFolder } from "./folderPicking";
import { sha256 } from "./hash";
import { UploadsContext, type FolderTarget, type UploadTarget, type Uploads } from "./uploadsContext";

type Job = {
  file: File;
  target: UploadTarget;
  hash?: string;
  onConflict: NameConflictPolicy;
  tus?: tus.Upload;
  /** Waiting for one of the two upload slots. */
  queued?: boolean;
  startedAt?: number;
  /** The folder upload it is part of. */
  group?: string;
};

type DuplicatePrompt = {
  ids: string[];
  total: number;
  matches: Map<string, DuplicateMatch>;
  group?: string;
  /** Name clashes among the other files, asked about once the duplicates are settled, so it's one question. */
  clashes: string[];
};
type ConflictPrompt = { ids: string[]; group?: string };
type FolderPrompt = { name: string; folder: string; answer: (choice: FolderConflictPolicy | "cancel") => void };
type QuotaPrompt = { name: string; size: number };

const chunkSize = 32 * 1024 * 1024;
const parallel = 2;
/** Files are hashed and checked in batches, so a big folder starts uploading before every file is hashed. */
const checkBatchFiles = 200;
const checkBatchBytes = 512 * 1024 * 1024;

/**
 * The upload queue (docs/design.md §8.1, §8.3): hash each file on the device, ask the server which ones the user
 * already has, settle duplicates and name clashes with the user, then send the rest over tus, two at a time. A folder
 * upload makes its folders first and shows as one row in the tray.
 */
export function UploadProvider({ children }: { children: ReactNode }) {
  const { t, i18n } = useTranslation();
  const toast = useSnackbar();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const [items, setItems] = useState<UploadItem[]>([]);
  const [groups, setGroups] = useState<UploadGroup[]>([]);
  const jobs = useRef(new Map<string, Job>());
  const results = useRef(new Map<string, string>());
  /** Each folder upload's top folder, to open it from the tray. */
  const groupFolders = useRef(new Map<string, string>());
  /** Folder uploads the person paused: their files wait, paused, instead of starting once they're checked. */
  const pausedGroups = useRef(new Set<string>());
  const [duplicates, setDuplicates] = useState<DuplicatePrompt[]>([]);
  const [conflicts, setConflicts] = useState<ConflictPrompt[]>([]);
  const [folderPrompts, setFolderPrompts] = useState<FolderPrompt[]>([]);
  const [quota, setQuota] = useState<QuotaPrompt | null>(null);

  const update = (id: string, patch: Partial<UploadItem>) =>
    setItems((list) => list.map((item) => (item.id === id ? { ...item, ...patch } : item)));
  const updateGroup = (id: string, patch: Partial<UploadGroup>) =>
    setGroups((list) => list.map((group) => (group.id === id ? { ...group, ...patch } : group)));
  const remove = (ids: string[]) => {
    ids.forEach((id) => jobs.current.delete(id));
    setItems((list) => list.filter((item) => !ids.includes(item.id)));
  };
  const groupName = (id: string | undefined) => groups.find((group) => group.id === id)?.name;
  const idsIn = (group: string) => [...jobs.current].filter(([, job]) => job.group === group).map(([id]) => id);

  const finished = (id: string, nodeId: string | undefined) => {
    const job = jobs.current.get(id);
    if (nodeId) results.current.set(id, nodeId);
    update(id, { state: "done", uploaded: job?.file.size, secondsLeft: undefined });
    if (job) {
      void invalidateFolders(queryClient, [job.target.folderId]);
      if ("nodeId" in job.target) void queryClient.invalidateQueries({ queryKey: ["versions", job.target.nodeId] });
    }
  };

  const failed = (id: string, error: unknown) => {
    const status = error instanceof tus.DetailedError ? error.originalResponse?.getStatus() : undefined;
    let message = t("upload.connectionLost");
    if (status) {
      let problem = {};
      try {
        problem = JSON.parse(error instanceof tus.DetailedError ? (error.originalResponse?.getBody() ?? "{}") : "{}");
      } catch {
        // Not a problem body; fall back to the generic message.
      }
      message = errorMessage(t, new ApiError(status, problem));
    } else if (error instanceof ApiError) {
      message = errorMessage(t, error);
    }
    update(id, { state: "failed", error: message, secondsLeft: undefined });
    pump();
  };

  /** Starts queued uploads until two are running. */
  const pump = () => {
    for (const [id, job] of jobs.current) {
      if ([...jobs.current.values()].filter((j) => j.tus).length >= parallel) break;
      if (job.queued) {
        job.queued = false;
        send(id);
      }
    }
  };

  const send = (id: string) => {
    const job = jobs.current.get(id);
    if (!job?.hash) return;
    const { file, target } = job;
    const metadata: Record<string, string> = {
      filename: file.name,
      filetype: file.type,
      sha256: job.hash,
      modifiedAt: String(file.lastModified),
      onConflict: job.onConflict,
    };
    if ("nodeId" in target) metadata.nodeId = target.nodeId;
    else if (target.folderId) metadata.folderId = target.folderId;

    job.startedAt = Date.now();
    job.tus = new tus.Upload(file, {
      endpoint: uploadsPath,
      chunkSize,
      retryDelays: [0, 1000, 3000, 10000],
      metadata,
      // Resume only an upload of the same bytes to the same place.
      fingerprint: async () => `dokument-${job.hash}-${"nodeId" in target ? target.nodeId : (target.folderId ?? "root")}-${file.name}`,
      removeFingerprintOnSuccess: true,
      onBeforeRequest: (request) => {
        const token = getCsrfToken();
        if (token) request.setHeader(csrfHeader, token);
      },
      onShouldRetry: (error) => {
        const status = error.originalResponse?.getStatus() ?? 0;
        // Refusals (name taken, quota, a changed file) won't go away by asking again.
        return status === 0 || status >= 500 || status === 423;
      },
      onProgress: (sent, total) => {
        const elapsed = (Date.now() - (job.startedAt ?? Date.now())) / 1000;
        const speed = elapsed > 1 ? sent / elapsed : 0;
        update(id, { uploaded: sent, secondsLeft: speed > 0 ? (total - sent) / speed : undefined });
      },
      onSuccess: ({ lastResponse }) => {
        job.tus = undefined;
        finished(id, lastResponse.getHeader(uploadNodeIdHeader) ?? undefined);
        pump();
      },
      onError: (error) => {
        job.tus = undefined;
        failed(id, error);
      },
    });
    job.tus.findPreviousUploads().then((previous) => {
      if (previous[0]) job.tus?.resumeFromPreviousUpload(previous[0]);
      job.tus?.start();
    }, () => job.tus?.start());
  };

  /** Queues files that passed the checks. */
  const enqueue = (ids: string[]) => {
    if (ids.length === 0) return;
    const held = new Set(ids.filter((id) => pausedGroups.current.has(jobs.current.get(id)?.group ?? "")));
    ids.forEach((id) => {
      const job = jobs.current.get(id);
      if (job && !held.has(id)) job.queued = true;
    });
    setItems((list) =>
      list.map((item) =>
        ids.includes(item.id)
          ? { ...item, state: held.has(item.id) ? "paused" : "uploading", uploaded: 0, error: undefined }
          : item,
      ),
    );
    pump();
  };

  const clashes = (id: string) => {
    const job = jobs.current.get(id);
    return !!job && "existingNames" in job.target && job.target.existingNames.has(nameKey(job.file.name));
  };

  /** Queues the files whose names are free and returns the ones that clash, to ask about. */
  const queueFree = (ids: string[]) => {
    enqueue(ids.filter((id) => !clashes(id)));
    return ids.filter(clashes);
  };

  const askAboutClashes = (ids: string[], group?: string) => {
    if (ids.length > 0) setConflicts((queue) => [...queue, { ids, group }]);
  };

  /** "Upload anyway" for files the user already has: no bytes are sent (§8.3). Returns those whose names clash. */
  const copy = async (ids: string[]): Promise<string[]> => {
    const clean: string[] = [];
    for (const id of ids) {
      const job = jobs.current.get(id);
      if (!job?.hash) continue;
      const target = job.target;
      if ("nodeId" in target || clashes(id)) {
        clean.push(id);
        continue;
      }
      try {
        const node = await drive.copyFromContent(
          target.folderId,
          job.file.name,
          job.hash,
          new Date(job.file.lastModified).toISOString(),
          "keepBoth",
        );
        finished(id, node.id);
      } catch (error) {
        failed(id, error);
      }
    }
    // Copies that would clash by name go through the normal path, which asks first.
    return queueFree(clean);
  };

  /**
   * Hashes the files and checks them for duplicates a batch at a time; new files with free names start uploading
   * at once. Duplicates and name clashes are asked about once at the end, for all the files together.
   */
  const prepare = async (ids: string[], group?: string) => {
    const matches = new Map<string, DuplicateMatch>();
    const clashing: string[] = [];
    let batch: string[] = [];
    let batchBytes = 0;

    const check = async () => {
      const checking = batch;
      batch = [];
      batchBytes = 0;
      if (checking.length === 0) return;
      try {
        const found = await drive.checkDuplicates(
          checking.map((id) => {
            const job = jobs.current.get(id)!;
            return { name: job.file.name, size: job.file.size, sha256: job.hash! };
          }),
        );
        const fresh: string[] = [];
        checking.forEach((id, i) => {
          const match = found[i]?.duplicates[0];
          if (match) matches.set(id, match);
          else fresh.push(id);
        });
        const checked = new Set(checking);
        setItems((list) => list.map((item) => (checked.has(item.id) ? { ...item, checked: true } : item)));
        clashing.push(...queueFree(fresh));
      } catch (error) {
        checking.forEach((id) => failed(id, error));
      }
    };

    for (const id of ids) {
      const job = jobs.current.get(id);
      if (!job) continue;
      try {
        job.hash = await sha256(job.file);
      } catch (error) {
        failed(id, error);
        continue;
      }
      // A new version of a known file skips the duplicate check: the server ignores identical bytes anyway.
      if ("nodeId" in job.target) {
        enqueue([id]);
        continue;
      }
      batch.push(id);
      batchBytes += job.file.size;
      if (batch.length >= checkBatchFiles || batchBytes >= checkBatchBytes) await check();
    }
    await check();

    // Files cancelled while they were being checked are no longer asked about.
    const live = (list: string[]) => list.filter((id) => jobs.current.has(id));
    const duplicateIds = live([...matches.keys()]);
    if (duplicateIds.length > 0) {
      setDuplicates((queue) => [
        ...queue,
        { ids: duplicateIds, total: ids.length, matches, group, clashes: live(clashing) },
      ]);
    } else askAboutClashes(live(clashing), group);
  };

  const addJobs = (files: { file: File; target: UploadTarget; name?: string; group?: string }[]) => {
    const stamp = Date.now();
    const added = files.map(({ file, target, name, group }, i) => {
      const id = `upload-${stamp}-${i}`;
      jobs.current.set(id, { file, target, onConflict: "fail", group });
      return {
        id,
        name: name ?? file.name,
        kind: kindOf(file.name, file.type),
        state: "hashing" as const,
        size: file.size,
        folder: target.folderName,
        group,
      };
    });
    setItems((list) => [...added, ...list]);
    return added.map((item) => item.id);
  };

  const start: Uploads["start"] = (files, target) => {
    void prepare(addJobs(files.map((file) => ({ file, target }))));
  };

  const askFolderConflict = (name: string, folder: string) =>
    new Promise<FolderConflictPolicy | "cancel">((answer) =>
      setFolderPrompts((queue) => [...queue, { name, folder, answer }]),
    );

  /**
   * A folder upload (§8.1): make the whole folder tree in one request, asking first if the top folder's name is
   * taken, then upload the files into it. Nothing is made if the files don't fit in the quota.
   */
  const uploadFolder = async (picked: PickedFolder, target: FolderTarget) => {
    const group = `folder-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
    setGroups((list) => [{ id: group, name: picked.name, folder: target.folderName, preparing: true }, ...list]);

    // Folders with names the server won't take are left out, and the files in them fail with a reason.
    const validPath = (path: string) => path.split("/").every(validName);
    const folders = picked.folders.filter(validPath);
    const files = picked.files.filter(({ file, dir }) => validPath(dir) && validName(file.name));
    const refused = picked.files.filter((entry) => !files.includes(entry));
    const size = files.reduce((sum, { file }) => sum + file.size, 0);

    if (!validName(picked.name)) {
      updateGroup(group, { preparing: false, error: t("errors.invalidName") });
      return;
    }

    let policy: FolderConflictPolicy = "fail";
    let tree;
    for (;;) {
      try {
        tree = await drive.createFolderTree(target.folderId, folders, policy, size);
        break;
      } catch (error) {
        const code = error instanceof ApiError ? error.problem.code : undefined;
        if (code === "nameTaken" && policy === "fail") {
          const choice = await askFolderConflict(picked.name, target.folderName);
          if (choice === "cancel") {
            setGroups((list) => list.filter((g) => g.id !== group));
            return;
          }
          policy = choice;
          continue;
        }
        if (code === "quotaExceeded") {
          setGroups((list) => list.filter((g) => g.id !== group));
          // The dialog says how much is left, so it needs today's numbers.
          await queryClient.fetchQuery({ ...storageQuery, staleTime: 0 }).catch(() => undefined);
          setQuota({ name: picked.name, size });
          return;
        }
        updateGroup(group, { preparing: false, error: errorMessage(t, error) });
        return;
      }
    }

    const byPath = new Map(tree.map((entry) => [entry.path, entry]));
    const top = byPath.get(picked.name)!;
    groupFolders.current.set(group, top.id);
    void invalidateFolders(queryClient, [target.folderId]);

    // Files show their path inside the folder: "2025/ikea.pdf".
    const inside = (dir: string, name: string) => [...dir.split("/").slice(1), name].join("/");
    const ids = addJobs(
      files.map(({ file, dir }) => {
        const folder = byPath.get(dir)!;
        return {
          file,
          name: inside(dir, file.name),
          group,
          target: {
            folderId: folder.id,
            folderName: folder.name,
            existingNames: new Set(folder.existingNames.map(nameKey)),
          },
        };
      }),
    );
    if (refused.length > 0) {
      setItems((list) => [
        ...refused.map(({ file, dir }, i) => ({
          id: `refused-${group}-${i}`,
          name: inside(dir, file.name),
          kind: kindOf(file.name, file.type),
          state: "failed" as const,
          size: file.size,
          folder: picked.name,
          group,
          error: t("errors.invalidName"),
          retryable: false,
        })),
        ...list,
      ]);
    }
    updateGroup(group, { name: top.name, preparing: false });
    await prepare(ids, group);
  };

  const startFolders: Uploads["startFolders"] = (folders, target) => {
    // One after another, so a second "already exists" question waits for the first.
    void (async () => {
      for (const folder of folders) await uploadFolder(folder, target);
    })();
  };

  const prompt = duplicates[0];
  const resolveDuplicates = (choice: "skip" | "upload" | "review") => {
    if (!prompt) return;
    setDuplicates((queue) => queue.slice(1));
    if (choice === "skip") {
      remove(prompt.ids);
      if (prompt.group) {
        const group = prompt.group;
        setGroups((list) =>
          list.map((g) => (g.id === group ? { ...g, skipped: (g.skipped ?? 0) + prompt.ids.length } : g)),
        );
      } else toast(t("toasts.skippedDuplicates", { count: prompt.ids.length }));
      askAboutClashes(prompt.clashes, prompt.group);
    } else if (choice === "upload") {
      void copy(prompt.ids).then((clashing) => askAboutClashes([...prompt.clashes, ...clashing], prompt.group));
    } else {
      // Review: ask about each file on its own; the name clashes come after the last one.
      const ids = prompt.ids;
      setDuplicates((queue) => [
        ...ids.map((id, i) => ({
          ids: [id],
          total: 1,
          matches: new Map([[id, prompt.matches.get(id)!]]),
          group: prompt.group,
          clashes: i === ids.length - 1 ? prompt.clashes : [],
        })),
        ...queue,
      ]);
    }
  };

  const clash = conflicts[0];
  const resolveConflicts = (policy: NameConflictPolicy | "skip") => {
    if (!clash) return;
    setConflicts((queue) => queue.slice(1));
    if (policy === "skip") {
      remove(clash.ids);
      return;
    }
    clash.ids.forEach((id) => {
      const job = jobs.current.get(id);
      if (job) job.onConflict = policy;
    });
    enqueue(clash.ids);
  };

  const pause = (id: string) => {
    const job = jobs.current.get(id);
    if (!job) return;
    void job.tus?.abort();
    job.tus = undefined;
    job.queued = false;
    update(id, { state: "paused", secondsLeft: undefined });
  };

  const resumeOrRetry = (id: string) => {
    const job = jobs.current.get(id);
    if (!job) return;
    if (!job.hash) {
      update(id, { state: "hashing", error: undefined });
      void prepare([id], job.group);
      return;
    }
    job.queued = true;
    update(id, { state: "uploading", error: undefined });
    pump();
  };

  const uploads: Uploads = {
    items,
    groups,
    start,
    startFolders,
    pause: (id) => {
      pause(id);
      pump();
    },
    // Both pick up where the bytes left off: tus asks the server for the offset.
    resume: (id) => resumeOrRetry(id),
    retry: (id) => resumeOrRetry(id),
    cancel: (id) => {
      void jobs.current.get(id)?.tus?.abort(true);
      remove([id]);
      pump();
    },
    pauseGroup: (group) => {
      // Files still being checked keep going; they queue up paused instead of starting.
      pausedGroups.current.add(group);
      for (const id of idsIn(group)) {
        const job = jobs.current.get(id)!;
        if (job.tus || job.queued) pause(id);
      }
      pump();
    },
    resumeGroup: (group) => {
      pausedGroups.current.delete(group);
      const paused = new Set(items.filter((item) => item.group === group && item.state === "paused").map((i) => i.id));
      idsIn(group)
        .filter((id) => paused.has(id))
        .forEach(resumeOrRetry);
    },
    retryGroup: (group) => {
      const failedIds = new Set(items.filter((item) => item.group === group && item.state === "failed").map((i) => i.id));
      idsIn(group)
        .filter((id) => failedIds.has(id))
        .forEach(resumeOrRetry);
    },
    dismissGroup: (group) => {
      remove(items.filter((item) => item.group === group).map((item) => item.id));
      setGroups((list) => list.filter((g) => g.id !== group));
    },
    clear: () => {
      jobs.current.forEach((job) => void job.tus?.abort());
      jobs.current.clear();
      setItems([]);
      setGroups([]);
    },
    resultOf: (id) => {
      const job = jobs.current.get(id);
      return job && { nodeId: results.current.get(id), folderId: job.target.folderId };
    },
    folderOf: (group) => groupFolders.current.get(group),
  };

  const promptMatches = prompt ? prompt.ids.map((id) => prompt.matches.get(id)!) : [];
  const clashName = clash?.ids[0] ? jobs.current.get(clash.ids[0])?.file.name : undefined;
  const clashFolder = groupName(clash?.group);
  const folderPrompt = folderPrompts[0];
  const answerFolder = (choice: FolderConflictPolicy | "cancel") => {
    if (!folderPrompt) return;
    setFolderPrompts((queue) => queue.slice(1));
    folderPrompt.answer(choice);
  };
  const storage = queryClient.getQueryData(storageQuery.queryKey);
  const free = storage?.quotaBytes != null ? Math.max(0, storage.quotaBytes - storage.usedBytes) : 0;

  return (
    <UploadsContext value={uploads}>
      {children}
      <DuplicateDialog
        open={!!prompt}
        onOpenChange={(open) => !open && resolveDuplicates("skip")}
        total={prompt?.total ?? 0}
        folder={groupName(prompt?.group)}
        duplicates={promptMatches.map((match) => ({
          name: match.name,
          kind: kindOf(match.name),
          path: pathLabel(t("nav.myFiles"), match.path),
          trashed: match.inTrash,
        }))}
        onSkip={() => resolveDuplicates("skip")}
        onUploadAll={() => resolveDuplicates("upload")}
        onSecondary={() => {
          // Single file: "Open existing" shows where it is.
          if (prompt && prompt.total === 1) {
            const folderId = promptMatches[0]?.path.at(-1)?.id;
            resolveDuplicates("skip");
            void (folderId
              ? navigate({ to: "/folders/$folderId", params: { folderId } })
              : navigate({ to: "/" }));
          } else resolveDuplicates("review");
        }}
      />
      <Dialog
        open={!!clash}
        onOpenChange={(open) => !open && resolveConflicts("skip")}
        title={
          clashFolder
            ? t("dialogs.conflict.titleIn", { count: clash?.ids.length ?? 0, name: clashName, folder: clashFolder })
            : t("dialogs.conflict.title", { count: clash?.ids.length ?? 0, name: clashName })
        }
        description={
          (clash?.ids.length ?? 0) > 1
            ? t("dialogs.conflict.bodyAll", { count: clash?.ids.length ?? 0 })
            : t("dialogs.conflict.body")
        }
        footer={
          <>
            <Button variant="text" onClick={() => resolveConflicts("skip")}>
              {t("dialogs.conflict.skip")}
            </Button>
            <Button variant="text" onClick={() => resolveConflicts("keepBoth")}>
              {t("dialogs.conflict.keepBoth")}
            </Button>
            <Button onClick={() => resolveConflicts("newVersion")}>
              {(clash?.ids.length ?? 0) > 1 ? t("dialogs.conflict.newVersions") : t("dialogs.conflict.newVersion")}
            </Button>
          </>
        }
      />
      <Dialog
        open={!!folderPrompt}
        onOpenChange={(open) => !open && answerFolder("cancel")}
        title={t("dialogs.folderExists.title", { name: folderPrompt?.name, folder: folderPrompt?.folder })}
        description={t("dialogs.folderExists.body")}
        footer={
          <>
            <Button variant="text" onClick={() => answerFolder("cancel")}>
              {t("common.cancel")}
            </Button>
            <Button variant="text" onClick={() => answerFolder("keepBoth")}>
              {t("dialogs.folderExists.keepBoth")}
            </Button>
            <Button onClick={() => answerFolder("merge")}>{t("dialogs.folderExists.merge")}</Button>
          </>
        }
      />
      <Dialog
        open={!!quota}
        onOpenChange={(open) => !open && setQuota(null)}
        title={t("dialogs.folderQuota.title", { name: quota?.name })}
        description={t("dialogs.folderQuota.body", {
          size: formatBytes(quota?.size ?? 0, i18n.language),
          free: formatBytes(free, i18n.language),
        })}
        footer={<Button onClick={() => setQuota(null)}>{t("dialogs.folderQuota.ok")}</Button>}
      />
    </UploadsContext>
  );
}
