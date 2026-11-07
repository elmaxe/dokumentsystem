import type { FilingRunRequest } from "@dokument/api";
import { Banner, Button, Checkbox, Dialog, FileTypeIcon, Icon, kindOf, useSnackbar } from "@dokument/ui";
import { keepPreviousData, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { errorMessage } from "../files/drive";
import { filingRulesQuery } from "./filing";
import { filingRuns, latestRunQuery, type RunScope } from "./filingRuns";

/**
 * The preview of a run, then starting it (§13.6): how many files would move, by folder and where each comes from, and
 * what stays. Files the person placed themselves stay unless they tick the box.
 */
export function RunFilingDialog({
  scope,
  onClose,
  onStarted,
}: {
  scope: RunScope | null;
  onClose: () => void;
  onStarted?: () => void;
}) {
  const { t } = useTranslation();
  const toast = useSnackbar();
  const queryClient = useQueryClient();
  const [includePlaced, setIncludePlaced] = useState(false);
  const [open, setOpen] = useState<string | null>(null);
  const [starting, setStarting] = useState(false);
  const request: FilingRunRequest = {
    ruleId: scope?.ruleId ?? null,
    nodeIds: scope?.nodeIds ?? null,
    includePlaced,
  };
  const preview = useQuery({
    queryKey: ["filing-run-preview", request],
    queryFn: () => filingRuns.preview(request),
    enabled: !!scope,
    placeholderData: keepPreviousData,
    retry: false,
    gcTime: 0,
  });
  const data = preview.data;
  // Ticking the box moves those files into the count of files that move, so remember how many there were.
  const [placed, setPlaced] = useState(0);
  if (data && !includePlaced && data.placed !== placed) setPlaced(data.placed);

  const title = scope?.ruleId
    ? t("filing.run.titleOne", { tag: scope.ruleTag ?? "" })
    : scope?.nodeIds
      ? t("filing.run.titleSelection", { count: scope.nodeIds.length })
      : t("filing.run.titleAll");
  const description = scope?.ruleId
    ? t("filing.run.scopeOne")
    : scope?.nodeIds
      ? t("filing.run.scopeSelection")
      : t("filing.run.scopeAll", { count: scope?.rules ?? 0 });

  const start = async () => {
    setStarting(true);
    try {
      const run = await filingRuns.start(request);
      queryClient.setQueryData(latestRunQuery.queryKey, run);
      await queryClient.invalidateQueries({ queryKey: filingRulesQuery.queryKey });
      toast(t("filing.run.started", { count: data?.move ?? 0 }));
      onStarted?.();
      onClose();
    } catch (error) {
      toast(errorMessage(t, error));
    } finally {
      setStarting(false);
    }
  };

  const stays = data
    ? (
        [
          ["check_circle", "alreadyThere", data.alreadyThere, "text-success"],
          ["group", "shared", data.shared, "text-on-surface-variant"],
          ["block", "blocked", data.blocked, "text-on-surface-variant"],
          ["person_off", "notYours", data.notYours, "text-on-surface-variant"],
          ...(scope?.nodeIds ? [["label_off", "noRule", data.noRule, "text-on-surface-variant"] as const] : []),
        ] as const
      ).filter(([, , count]) => count > 0)
    : [];

  return (
    <Dialog
      open={!!scope}
      onOpenChange={(isOpen) => !isOpen && onClose()}
      title={title}
      description={description}
      footer={
        data && data.move === 0 ? (
          <Button onClick={onClose}>{t("filing.run.close")}</Button>
        ) : (
          <>
            <Button variant="outlined" onClick={onClose}>
              {t("common.cancel")}
            </Button>
            <Button disabled={!data || starting || preview.isFetching} onClick={() => void start()}>
              {t("filing.run.start", { count: data?.move ?? 0 })}
            </Button>
          </>
        )
      }
    >
      {preview.isError ? (
        <Banner tone="error">{errorMessage(t, preview.error) || t("filing.run.failed")}</Banner>
      ) : !data ? (
        <p className="m-0 flex items-center gap-2 text-body-md text-on-surface-variant">
          <Icon name="progress_activity" className="animate-spin" />
          {t("filing.run.loading")}
        </p>
      ) : (
        <div className="flex flex-col gap-4" aria-live="polite">
          <div className="flex flex-col rounded-md bg-surface-container-lowest py-1">
            <span className="flex items-center gap-3 px-4 py-3 text-title-sm">
              <Icon name={data.move > 0 ? "drive_file_move" : "check_circle"} className={data.move > 0 ? "text-primary" : "text-success"} />
              {data.move > 0
                ? t("filing.run.move", { count: data.move })
                : placed > 0 || stays.some(([, key]) => key !== "alreadyThere" && key !== "noRule")
                  ? t("filing.run.noneMove")
                  : t("filing.run.nothing")}
            </span>
            {data.groups.map((group) => {
              const expanded = open === group.folder;
              return (
                <div key={group.folder} className="flex flex-col">
                  <button
                    type="button"
                    aria-expanded={expanded}
                    onClick={() => setOpen(expanded ? null : group.folder)}
                    className="flex min-h-10 cursor-pointer items-center gap-3 py-2 pr-3 pl-12 text-left text-body-md hover:bg-on-surface/8"
                  >
                    <Icon name="folder" size={18} className="shrink-0 text-on-surface-variant" />
                    <span className="min-w-0 flex-1 truncate">
                      {group.folder}
                      {group.newFolder && (
                        <span className="text-body-sm text-on-surface-variant"> · {t("filing.run.newFolder")}</span>
                      )}
                    </span>
                    <span className="tabular shrink-0 text-on-surface-variant">
                      {t("filing.run.files", { count: group.count })}
                    </span>
                    <Icon name={expanded ? "expand_less" : "expand_more"} className="text-on-surface-variant" />
                  </button>
                  {expanded && (
                    <ul className="m-0 flex max-h-60 list-none flex-col gap-1.5 overflow-y-auto py-1 pr-4 pl-19">
                      {group.files.map((file) => (
                        <li key={file.nodeId} className="flex min-w-0 items-center gap-2 text-body-sm">
                          <FileTypeIcon kind={kindOf(file.name, file.mimeType)} size="sm" />
                          <span className="min-w-0 flex-1 truncate text-on-surface">{file.name}</span>
                          <span className="max-w-[45%] shrink-0 truncate text-on-surface-variant">
                            {t("filing.run.from", { folder: file.from || t("filing.run.top") })}
                          </span>
                        </li>
                      ))}
                      {group.count > group.files.length && (
                        <li className="text-body-sm text-on-surface-variant">
                          {t("filing.run.more", { count: group.count - group.files.length })}
                        </li>
                      )}
                    </ul>
                  )}
                </div>
              );
            })}
            {stays.length > 0 && <div className="my-1 border-t border-outline-variant" />}
            {stays.map(([icon, key, count, tone]) => (
              <span key={key} className="flex items-start gap-3 px-4 py-2 text-body-md text-on-surface-variant">
                <Icon name={icon} className={`shrink-0 ${tone}`} />
                {t(`filing.run.${key}`, { count })}
              </span>
            ))}
          </div>
          {(placed > 0 || includePlaced) && (
            <label className="flex cursor-pointer items-start gap-3">
              <span className="pt-0.5">
                <Checkbox
                  checked={includePlaced}
                  onCheckedChange={setIncludePlaced}
                  label={t("filing.run.placed", { count: placed })}
                />
              </span>
              <span className="flex flex-col">
                <span className="text-title-sm text-on-surface">{t("filing.run.placed", { count: placed })}</span>
                <span className="text-body-sm text-on-surface-variant">{t("filing.run.placedHint")}</span>
              </span>
            </label>
          )}
        </div>
      )}
    </Dialog>
  );
}
