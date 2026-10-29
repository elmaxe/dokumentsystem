import type { Job, JobFile, JobKind, JobQueue } from "@dokument/api";
import { formatBytes } from "@dokument/core";
import {
  ActionMenu,
  Avatar,
  Banner,
  Button,
  Chip,
  FileTypeIcon,
  Icon,
  IconButton,
  kindOf,
  Progress,
  useFormat,
  useSnackbar,
} from "@dokument/ui";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { sessionQuery } from "../api";
import { AdminFrame } from "./AdminPage";
import { jobQueuesQuery, jobs, jobsQuery, type JobFilter } from "./jobsApi";

const queueIcons: Record<JobKind, string> = {
  thumbnail: "image",
  metadata: "info",
  text: "document_scanner",
  transcode: "movie",
  transcribe: "graphic_eq",
  tagRule: "sell",
  file: "drive_file_move",
  filingRule: "drive_file_move",
};
const pageSize = 50;

/**
 * What the worker is doing (docs/design.md §10.1): a card per queue with pause, then the running, waiting or failed
 * jobs as the files they're for. Admins see every file's name, whoever owns it (decision 25).
 */
export function AdminJobsPage() {
  const { t } = useTranslation();
  const queues = useQuery(jobQueuesQuery);
  const [filter, setFilter] = useState<JobFilter>({ status: "running", limit: pageSize });
  const list = useQuery(jobsQuery(filter));
  const toast = useSnackbar();
  const queryClient = useQueryClient();
  const now = useNow();

  const total = (key: "running" | "waiting" | "failed") =>
    (queues.data ?? []).filter((q) => !filter.type || q.type === filter.type).reduce((sum, q) => sum + q[key], 0);
  const refresh = () => queryClient.invalidateQueries({ queryKey: ["admin", "jobs"] });
  const act = async (run: () => Promise<unknown>, done: string) => {
    try {
      await run();
      await refresh();
      toast(done);
    } catch {
      toast(t("errors.generic"));
    }
  };

  const statuses = [
    { id: "running", count: total("running") },
    { id: "queued", count: total("waiting") },
    { id: "failed", count: total("failed") },
  ] as const;

  return (
    <AdminFrame title={t("admin.nav.jobs")}>
      <p className="m-0 max-w-160 text-body-md text-on-surface-variant">{t("admin.jobs.intro")}</p>
      {queues.isError && <Banner tone="error">{t("errors.generic")}</Banner>}
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-4">
        {queues.data?.map((queue) => (
          <QueueCard
            key={queue.type}
            queue={queue}
            onPause={(paused) =>
              void act(() => jobs.pause(queue.type, paused), paused ? t("admin.jobs.paused") : t("common.saved"))
            }
          />
        ))}
      </div>

      <div className="flex flex-wrap items-center gap-2">
        {statuses.map(({ id, count }) => (
          <Chip
            key={id}
            selected={filter.status === id}
            onClick={() => setFilter({ ...filter, status: id, limit: pageSize })}
          >
            <span className="tabular">
              {t(`admin.jobs.filters.${id}`)} · {count.toLocaleString()}
            </span>
          </Chip>
        ))}
        <span aria-hidden className="mx-1 h-6 w-px bg-outline-variant" />
        <ActionMenu
          align="start"
          actions={[
            { id: "all", icon: !filter.type ? "check" : "", label: t("admin.jobs.allTypes") },
            ...(["thumbnail", "metadata", "text", "transcode", "transcribe", "tagRule", "file", "filingRule"] as const).map((type) => ({
              id: type,
              icon: filter.type === type ? "check" : "",
              label: t(`admin.jobs.queues.${type}`),
            })),
          ]}
          onAction={(id) => setFilter({ ...filter, type: id === "all" ? undefined : id, limit: pageSize })}
          trigger={
            <span className="inline-flex">
              <Chip selected={!!filter.type}>
                {filter.type ? t(`admin.jobs.queues.${filter.type}`) : t("admin.jobs.allTypes")}
                <Icon name="arrow_drop_down" size={18} />
              </Chip>
            </span>
          }
        />
        {filter.status === "failed" && total("failed") > 0 && (
          <Button
            icon="replay"
            className="ml-auto"
            onClick={() =>
              void act(() => jobs.retryFailed(filter.type), t("admin.jobs.retriedAll", { count: total("failed") }))
            }
          >
            {t("admin.jobs.retryAll")}
          </Button>
        )}
      </div>

      {list.isPending ? (
        <Progress label={t("admin.nav.jobs")} />
      ) : list.isError ? (
        <Banner tone="error">{t("errors.generic")}</Banner>
      ) : list.data.items.length === 0 ? (
        <p className="m-0 rounded-lg bg-surface-container-low p-6 text-center text-body-md text-on-surface-variant">
          {t(`admin.jobs.empty.${filter.status}`)}
        </p>
      ) : (
        <ul
          aria-label={t(`admin.jobs.filters.${filter.status}`)}
          className="m-0 flex list-none flex-col divide-y divide-outline-variant overflow-hidden rounded-lg bg-surface-container-low p-0"
        >
          {list.data.items.map((job) => (
            <JobRow
              key={job.id}
              job={job}
              now={now}
              onRetry={() => void act(() => jobs.retry(job.id), t("admin.jobs.retried"))}
              onClear={() => void act(() => jobs.clear(job.id), t("admin.jobs.cleared"))}
            />
          ))}
        </ul>
      )}
      {list.data?.more && (
        <Button
          variant="text"
          className="self-center"
          onClick={() => setFilter({ ...filter, limit: filter.limit + pageSize })}
        >
          {t("admin.jobs.showMore")}
        </Button>
      )}
    </AdminFrame>
  );
}

function QueueCard({ queue, onPause }: { queue: JobQueue; onPause: (paused: boolean) => void }) {
  const { t, i18n } = useTranslation();
  const name = t(`admin.jobs.queues.${queue.type}`);
  const number = (n: number) => n.toLocaleString(i18n.language);
  return (
    <section aria-label={name} className="flex flex-col gap-3 rounded-lg bg-surface-container-low p-4">
      <div className="flex items-center gap-2">
        <Icon name={queueIcons[queue.type]} className="text-primary" />
        <h2 className="m-0 min-w-0 flex-1 truncate text-title-md">{name}</h2>
      </div>
      <dl className="m-0 flex gap-6">
        {(
          [
            ["running", queue.running],
            ["waiting", queue.waiting],
            ["failed", queue.failed],
          ] as const
        ).map(([key, value]) => (
          <div key={key} className="flex flex-col-reverse">
            <dt className={`text-body-sm ${key === "failed" && value > 0 ? "text-error" : "text-on-surface-variant"}`}>
              {t(`admin.jobs.${key}`)}
            </dt>
            <dd className={`tabular m-0 text-headline-sm ${key === "failed" && value > 0 ? "text-error" : ""}`}>
              {number(value)}
            </dd>
          </div>
        ))}
      </dl>
      <div className="mt-auto flex items-center gap-2">
        <span className="flex min-w-0 flex-1 items-start gap-1.5 text-body-sm text-on-surface-variant">
          {queue.paused ? (
            <>
              <Icon name="pause_circle" size={16} className="mt-0.5 shrink-0 text-warning" />
              {t("admin.jobs.pausedNote")}
            </>
          ) : (
            t("admin.jobs.doneLastWeek", { count: queue.doneLastWeek })
          )}
        </span>
        <Button
          variant={queue.paused ? "tonal" : "outlined"}
          icon={queue.paused ? "play_arrow" : "pause"}
          aria-label={t(queue.paused ? "admin.jobs.resumeQueue" : "admin.jobs.pauseQueue", { queue: name })}
          onClick={() => onPause(!queue.paused)}
          className="h-9 gap-1.5 px-3.5"
        >
          {t(queue.paused ? "admin.jobs.resume" : "admin.jobs.pause")}
        </Button>
      </div>
    </section>
  );
}

function JobRow({ job, now, onRetry, onClear }: { job: Job; now: number; onRetry: () => void; onClear: () => void }) {
  const { t, i18n } = useTranslation();
  const format = useFormat();
  const session = useQuery(sessionQuery);
  const me = session.data?.user.id;
  const file = job.files[0] as JobFile | undefined;
  const kind = file ? kindOf(file.name, job.mimeType) : "other";
  const tagRule = job.type === "tagRule";
  const rule = tagRule || job.type === "filingRule";
  const mine = file?.ownerId === me;
  const when = (at: string) => format.date(at, { inSentence: true });

  const where = !file
    ? undefined
    : [
        file.use === "trashed"
          ? t("admin.jobs.trashed")
          : [mine ? t("nav.myFiles") : t("admin.jobs.theirFiles", { name: file.ownerName }), ...file.path.map((p) => p.name)].join(
              " / ",
            ),
        file.use === "olderVersion" && t("admin.jobs.olderVersion", { number: file.versionNumber }),
        job.size !== null && job.size !== undefined && formatBytes(job.size, i18n.language),
      ]
        .filter(Boolean)
        .join(" · ");

  const status =
    job.status === "running"
      ? t("admin.jobs.runningFor", { duration: duration(now - Date.parse(job.startedAt ?? job.createdAt)) })
      : job.status === "failed"
        ? t("admin.jobs.gaveUp", { count: job.attempts, time: when(job.finishedAt ?? job.createdAt) })
        : job.attempts > 0
          ? t("admin.jobs.retrying", { count: job.attempts, time: when(job.runAfter) })
          : t("admin.jobs.waitingSince", { time: when(job.createdAt) });

  return (
    <li className="flex flex-wrap items-center gap-x-4 gap-y-2 px-4 py-3 md:grid md:grid-cols-[minmax(0,1fr)_8.5rem_8rem_minmax(0,13rem)_5.5rem]">
      <div className="flex min-w-0 flex-[1_1_100%] items-center gap-3">
        <span className="flex size-10 shrink-0 items-center justify-center rounded-sm bg-surface-container-high">
          {rule ? <Icon name={queueIcons[job.type]} size={20} className="text-primary" /> : <FileTypeIcon kind={kind} size="sm" />}
        </span>
        <div className="flex min-w-0 flex-col">
          <span className="truncate text-body-md text-on-surface">
            {tagRule ? (
              job.ruleTag ? (
                <Link to="/admin/tags" className="text-on-surface underline-offset-2 hover:underline">
                  {t("admin.jobs.ruleRun", { tag: job.ruleTag })}
                </Link>
              ) : (
                <span className="text-on-surface-variant">{t("admin.jobs.ruleDeleted")}</span>
              )
            ) : rule ? (
              // Filing rules are each person's own, so there is nowhere for an admin to open one.
              job.ruleTag ? (
                t("admin.jobs.filingRun", { tag: job.ruleTag })
              ) : (
                <span className="text-on-surface-variant">{t("admin.jobs.filingDeleted")}</span>
              )
            ) : file && mine && file.use === "current" ? (
              <Link
                to="/files/$fileId"
                params={{ fileId: file.nodeId }}
                title={t("admin.jobs.open", { name: file.name })}
                className="text-on-surface underline-offset-2 hover:underline"
              >
                {file.name}
              </Link>
            ) : file ? (
              file.name
            ) : (
              <span className="text-on-surface-variant">{t("admin.jobs.noFile")}</span>
            )}
            {job.files.length > 1 && (
              <span className="text-on-surface-variant"> {t("admin.jobs.andMore", { count: job.files.length - 1 })}</span>
            )}
          </span>
          {rule ? (
            <span className="truncate text-body-sm text-on-surface-variant">{t(tagRule ? "admin.jobs.ruleRunNote" : "admin.jobs.filingRunNote")}</span>
          ) : (
            where && <span className="truncate text-body-sm text-on-surface-variant">{where}</span>
          )}
        </div>
      </div>
      <div className="flex min-w-0 items-center gap-2">
        {file && (
          <>
            <Avatar name={file.ownerName} size="sm" tone={mine ? 1 : 3} />
            <span className="truncate">{mine ? t("admin.jobs.you") : file.ownerName}</span>
          </>
        )}
      </div>
      <span className="text-body-md">{t(`admin.jobs.queues.${job.type}`)}</span>
      <div className="flex min-w-0 flex-[1_1_12rem] flex-col">
        <span className={`flex items-center gap-2 text-body-md ${job.status === "failed" ? "text-error" : ""}`}>
          {job.status === "running" && (
            <span aria-hidden className="inline-flex text-primary motion-safe:animate-spin">
              <Icon name="progress_activity" size={18} />
            </span>
          )}
          {job.status === "failed" && <Icon name="error" filled size={18} />}
          <span className="tabular">{status}</span>
        </span>
        {job.status === "queued" && job.priority === "background" && (
          <span className="text-body-sm text-on-surface-variant">{t("admin.jobs.backfill")}</span>
        )}
        {job.error && job.status !== "running" && (
          <code className="mt-1 truncate font-mono text-mono-sm text-on-surface-variant" title={job.error}>
            {job.error}
          </code>
        )}
      </div>
      <div className="ml-auto flex shrink-0 justify-end gap-1">
        {job.status === "failed" && (
          <>
            <IconButton icon="replay" label={t("admin.jobs.retry")} onClick={onRetry} />
            <IconButton icon="close" label={t("admin.jobs.clear")} onClick={onClear} />
          </>
        )}
      </div>
    </li>
  );
}

/** Re-renders every second, so "Running for 0:42" counts up between refreshes. */
function useNow() {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 1_000);
    return () => window.clearInterval(timer);
  }, []);
  return now;
}

/** "0:42", "12:05" or "1:02:09". */
function duration(ms: number) {
  const total = Math.max(0, Math.floor(ms / 1000));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = String(total % 60).padStart(2, "0");
  return h > 0 ? `${h}:${String(m).padStart(2, "0")}:${s}` : `${m}:${s}`;
}
