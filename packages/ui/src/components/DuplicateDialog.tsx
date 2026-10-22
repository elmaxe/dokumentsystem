import { Trans, useTranslation } from "react-i18next";
import type { FileKind } from "../lib/files";
import { Button } from "./Button";
import { Dialog, type DialogVariant } from "./Dialog";
import { FileTypeIcon } from "./FileTypeIcon";

export type DuplicateMatch = {
  name: string;
  kind: FileKind;
  /** Where the same content already is, limited to files you can access (§8.3). */
  path: string;
  trashed?: boolean;
};

export type DuplicateDialogProps = {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** How many files were picked. One file gets the single-file wording and buttons. */
  total: number;
  duplicates: DuplicateMatch[];
  onSkip: () => void;
  onUploadAll: () => void;
  /** Batch: review them one by one. Single file: open the existing copy. */
  onSecondary: () => void;
  variant?: DialogVariant;
};

/**
 * The pre-upload duplicate prompt (§8.3). A batch offers Skip duplicates / Upload all / Review;
 * a single file offers Skip / Upload anyway / Open existing (§16.4).
 */
export function DuplicateDialog({
  open,
  onOpenChange,
  total,
  duplicates,
  onSkip,
  onUploadAll,
  onSecondary,
  variant,
}: DuplicateDialogProps) {
  const { t } = useTranslation();
  const single = total === 1;
  const first = duplicates[0];
  const where = (match: DuplicateMatch) =>
    match.trashed ? t("duplicates.inTrash", { path: match.path }) : match.path;

  return (
    <Dialog
      open={open}
      onOpenChange={onOpenChange}
      variant={variant}
      title={
        single
          ? t("duplicates.singleTitle")
          : t("duplicates.batchTitle", { count: duplicates.length, total })
      }
      footer={
        single ? (
          <>
            <Button variant="text" onClick={onSecondary}>
              {t("duplicates.openExisting")}
            </Button>
            <Button variant="outlined" onClick={onUploadAll}>
              {t("duplicates.uploadAnyway")}
            </Button>
            <Button onClick={onSkip}>{t("duplicates.skip")}</Button>
          </>
        ) : (
          <>
            <Button variant="text" onClick={onSecondary}>
              {t("duplicates.review")}
            </Button>
            <Button variant="outlined" onClick={onUploadAll}>
              {t("duplicates.uploadAll")}
            </Button>
            <Button onClick={onSkip}>{t("duplicates.skipDuplicates")}</Button>
          </>
        )
      }
    >
      {single && first ? (
        <p className="m-0">
          <Trans
            i18nKey="duplicates.singleBody"
            values={{ name: first.name, path: where(first) }}
            components={{ b: <b className="font-semibold text-on-surface" /> }}
          />
        </p>
      ) : (
        <>
          <p className="m-0">{t("duplicates.batchBody")}</p>
          <ul className="m-0 flex list-none flex-col gap-2 p-0">
            {duplicates.slice(0, 3).map((match) => (
              <li key={match.name} className="flex min-w-0 items-center gap-3">
                <FileTypeIcon kind={match.kind} />
                <div className="flex min-w-0 flex-col">
                  <span className="truncate font-medium text-on-surface">{match.name}</span>
                  <span className="truncate text-body-sm">
                    {t("duplicates.in", { path: where(match) })}
                  </span>
                </div>
              </li>
            ))}
            {duplicates.length > 3 && (
              <li className="pl-9 text-body-sm">
                {t("duplicates.andMore", { count: duplicates.length - 3 })}
              </li>
            )}
          </ul>
        </>
      )}
    </Dialog>
  );
}
