import { csrfHeader, uploadNodeIdHeader, uploadsPath, type DuplicateMatch, type NameConflictPolicy } from "@dokument/api";
import {
  Button,
  Dialog,
  DuplicateDialog,
  kindOf,
  useSnackbar,
  type UploadItem,
} from "@dokument/ui";
import { useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useRef, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import * as tus from "tus-js-client";
import { getCsrfToken } from "../api";
import { ApiError, drive, errorMessage, invalidateFolders, nameKey, pathLabel } from "./drive";
import { UploadsContext, type UploadTarget, type Uploads } from "./uploadsContext";
import { sha256 } from "./hash";

type Job = {
  file: File;
  target: UploadTarget;
  hash?: string;
  onConflict: NameConflictPolicy;
  tus?: tus.Upload;
  /** Waiting for one of the two upload slots. */
  queued?: boolean;
  startedAt?: number;
};

type DuplicatePrompt = { ids: string[]; total: number; matches: Map<string, DuplicateMatch> };

const chunkSize = 32 * 1024 * 1024;
const parallel = 2;

/**
 * The upload queue (docs/design.md §8.1, §8.3): hash each file on the device, ask the server which ones the user
 * already has, settle duplicates and name clashes with the user, then send the rest over tus, two at a time.
 */
export function UploadProvider({ children }: { children: ReactNode }) {
  const { t } = useTranslation();
  const toast = useSnackbar();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const [items, setItems] = useState<UploadItem[]>([]);
  const jobs = useRef(new Map<string, Job>());
  const results = useRef(new Map<string, string>());
  const [duplicates, setDuplicates] = useState<DuplicatePrompt[]>([]);
  const [conflicts, setConflicts] = useState<string[][]>([]);

  const update = (id: string, patch: Partial<UploadItem>) =>
    setItems((list) => list.map((item) => (item.id === id ? { ...item, ...patch } : item)));
  const remove = (ids: string[]) => {
    ids.forEach((id) => jobs.current.delete(id));
    setItems((list) => list.filter((item) => !ids.includes(item.id)));
  };

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
    ids.forEach((id) => {
      const job = jobs.current.get(id);
      if (job) job.queued = true;
    });
    setItems((list) =>
      list.map((item) => (ids.includes(item.id) ? { ...item, state: "uploading", uploaded: 0, error: undefined } : item)),
    );
    pump();
  };

  /** After duplicates are settled: ask about name clashes, then queue. */
  const checkNames = (ids: string[]) => {
    const clashing = ids.filter((id) => {
      const target = jobs.current.get(id)?.target;
      return target && "existingNames" in target && target.existingNames.has(nameKey(jobs.current.get(id)!.file.name));
    });
    enqueue(ids.filter((id) => !clashing.includes(id)));
    if (clashing.length > 0) setConflicts((queue) => [...queue, clashing]);
  };

  /** "Upload anyway" for a file the user already has: no bytes are sent (§8.3). */
  const copy = async (ids: string[]) => {
    const clean: string[] = [];
    for (const id of ids) {
      const job = jobs.current.get(id);
      if (!job?.hash) continue;
      const target = job.target;
      if ("existingNames" in target && target.existingNames.has(nameKey(job.file.name))) {
        clean.push(id);
        continue;
      }
      if ("nodeId" in target) {
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
    checkNames(clean);
  };

  const start: Uploads["start"] = (files, target) => {
    const stamp = Date.now();
    const added = files.map((file, i) => {
      const id = `upload-${stamp}-${i}`;
      jobs.current.set(id, { file, target, onConflict: "fail" });
      return {
        id,
        name: file.name,
        kind: kindOf(file.name, file.type),
        state: "hashing" as const,
        size: file.size,
        folder: target.folderName,
      };
    });
    setItems((list) => [...added, ...list]);

    void (async () => {
      const ids = added.map((item) => item.id);
      try {
        for (const id of ids) {
          const job = jobs.current.get(id);
          if (job) job.hash = await sha256(job.file);
        }
      } catch (error) {
        ids.forEach((id) => failed(id, error));
        return;
      }

      // A new version of a known file skips the duplicate check: the server ignores identical bytes anyway.
      if ("nodeId" in target) {
        enqueue(ids);
        return;
      }

      let results;
      try {
        results = await drive.checkDuplicates(
          ids.map((id) => {
            const job = jobs.current.get(id)!;
            return { name: job.file.name, size: job.file.size, sha256: job.hash! };
          }),
        );
      } catch (error) {
        ids.forEach((id) => failed(id, error));
        return;
      }

      const matches = new Map<string, DuplicateMatch>();
      ids.forEach((id, i) => {
        const match = results[i]?.duplicates[0];
        if (match) matches.set(id, match);
      });
      checkNames(ids.filter((id) => !matches.has(id)));
      if (matches.size > 0) setDuplicates((queue) => [...queue, { ids: [...matches.keys()], total: ids.length, matches }]);
    })();
  };

  const prompt = duplicates[0];
  const resolveDuplicates = (choice: "skip" | "upload" | "review") => {
    if (!prompt) return;
    setDuplicates((queue) => queue.slice(1));
    if (choice === "skip") {
      remove(prompt.ids);
      toast(t("toasts.skippedDuplicates", { count: prompt.ids.length }));
    } else if (choice === "upload") {
      void copy(prompt.ids);
    } else {
      // Review: ask about each file on its own.
      setDuplicates((queue) => [
        ...prompt.ids.map((id) => ({ ids: [id], total: 1, matches: new Map([[id, prompt.matches.get(id)!]]) })),
        ...queue,
      ]);
    }
  };

  const clash = conflicts[0];
  const resolveConflicts = (policy: NameConflictPolicy | "skip") => {
    if (!clash) return;
    setConflicts((queue) => queue.slice(1));
    if (policy === "skip") {
      remove(clash);
      return;
    }
    clash.forEach((id) => {
      const job = jobs.current.get(id);
      if (job) job.onConflict = policy;
    });
    enqueue(clash);
  };

  const resumeOrRetry = (id: string) => {
    const job = jobs.current.get(id);
    if (!job) return;
    if (!job.hash) {
      remove([id]);
      start([job.file], job.target);
      return;
    }
    job.queued = true;
    update(id, { state: "uploading", error: undefined });
    pump();
  };

  const uploads: Uploads = {
    items,
    start,
    pause: (id) => {
      const job = jobs.current.get(id);
      if (!job) return;
      void job.tus?.abort();
      job.tus = undefined;
      job.queued = false;
      update(id, { state: "paused", secondsLeft: undefined });
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
    clear: () => {
      jobs.current.forEach((job) => void job.tus?.abort());
      jobs.current.clear();
      setItems([]);
    },
    resultOf: (id) => {
      const job = jobs.current.get(id);
      return job && { nodeId: results.current.get(id), folderId: job.target.folderId };
    },
  };

  const promptMatches = prompt ? prompt.ids.map((id) => prompt.matches.get(id)!) : [];
  const clashName = clash?.[0] ? jobs.current.get(clash[0])?.file.name : undefined;

  return (
    <UploadsContext value={uploads}>
      {children}
      <DuplicateDialog
        open={!!prompt}
        onOpenChange={(open) => !open && resolveDuplicates("skip")}
        total={prompt?.total ?? 0}
        duplicates={promptMatches.map((match) => ({
          name: match.name,
          kind: kindOf(match.name),
          path: pathLabel(t("nav.myFiles"), match.path),
          trashed: match.inTrash,
        }))}
        onSkip={() => resolveDuplicates("skip")}
        onUploadAll={() => resolveDuplicates("upload")}
        onSecondary={() => {
          // Single file: "Open existing" shows where it is; there's no viewer until previews arrive.
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
        title={t("dialogs.conflict.title", { count: clash?.length ?? 0, name: clashName })}
        description={t("dialogs.conflict.body")}
        footer={
          <>
            <Button variant="text" onClick={() => resolveConflicts("skip")}>
              {t("dialogs.conflict.skip")}
            </Button>
            <Button variant="text" onClick={() => resolveConflicts("keepBoth")}>
              {t("dialogs.conflict.keepBoth")}
            </Button>
            <Button onClick={() => resolveConflicts("newVersion")}>{t("dialogs.conflict.newVersion")}</Button>
          </>
        }
      />
    </UploadsContext>
  );
}
