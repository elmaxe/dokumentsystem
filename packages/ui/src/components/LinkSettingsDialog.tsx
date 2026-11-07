import { useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { useFormat } from "../lib/display";
import { Button } from "./Button";
import { Dialog, type DialogVariant } from "./Dialog";
import { Icon } from "./Icon";
import { lastDay } from "../lib/links";
import type { ShareLink } from "./ShareDialog";
import { Switch } from "./Switch";
import { TextField } from "./TextField";

/** What saving the link settings asks for. */
export type LinkSettingsChange = {
  /** The last day the link works, as yyyy-mm-dd; null for never. */
  lastDay: string | null;
  allowDownload: boolean;
  /** A new password; undefined keeps the one it has. */
  password?: string;
  removePassword: boolean;
};

export type LinkSettingsDialogProps = {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  name: string;
  isFolder: boolean;
  link: ShareLink & { url?: string | null; opens: number; lastOpenedAt?: string | null };
  onCopy: () => void;
  onSave: (change: LinkSettingsChange) => void;
  onDelete: () => void;
  /** Why the last save was refused. */
  error?: string;
  busy?: boolean;
  variant?: DialogVariant;
};

/** yyyy-mm-dd for a local day. */
const isoDay = (day: Date) =>
  `${day.getFullYear()}-${String(day.getMonth() + 1).padStart(2, "0")}-${String(day.getDate()).padStart(2, "0")}`;

/**
 * A public link's expiry, password and download setting, its address and how often it was opened (§9, §16.4). Opened
 * from the share dialog's Link settings.
 */
export function LinkSettingsDialog({
  open,
  onOpenChange,
  name,
  isFolder,
  link,
  onCopy,
  onSave,
  onDelete,
  error,
  busy,
  variant,
}: LinkSettingsDialogProps) {
  const { t } = useTranslation();
  const format = useFormat();
  const tomorrow = new Date();
  tomorrow.setDate(tomorrow.getDate() + 1);
  const [expires, setExpires] = useState(!!link.expiresAt);
  const [day, setDay] = useState(() => {
    if (link.expiresAt) return isoDay(lastDay(link.expiresAt));
    const inAMonth = new Date();
    inAMonth.setDate(inAMonth.getDate() + 30);
    return isoDay(inAMonth);
  });
  const [protect, setProtect] = useState(link.hasPassword);
  const [changing, setChanging] = useState(!link.hasPassword);
  const [password, setPassword] = useState("");
  const [allowDownload, setAllowDownload] = useState(link.allowDownload);
  const [missing, setMissing] = useState(false);

  const save = () => {
    if (protect && changing && !password) {
      setMissing(true);
      return;
    }
    onSave({
      lastDay: expires && day ? day : null,
      allowDownload,
      password: protect && changing ? password : undefined,
      removePassword: !protect && link.hasPassword,
    });
  };

  const row = (icon: string, title: string, hint: string, control: ReactNode) => (
    <div className="flex min-w-0 items-center gap-3 px-4 py-3">
      <Icon name={icon} className="shrink-0 text-on-surface-variant" />
      <div className="flex min-w-0 flex-1 flex-col">
        <span className="font-medium text-on-surface">{title}</span>
        <span className="text-body-sm">{hint}</span>
      </div>
      {control}
    </div>
  );

  return (
    <Dialog
      open={open}
      onOpenChange={onOpenChange}
      variant={variant}
      title={t("share.linkTo", { name })}
      footer={
        <>
          <Button variant="text" icon="link_off" onClick={onDelete} disabled={busy} className="mr-auto !text-error">
            {t("share.deleteLink")}
          </Button>
          <Button variant="outlined" onClick={() => onOpenChange(false)}>
            {t("common.cancel")}
          </Button>
          <Button onClick={save} disabled={busy}>
            {t("share.saveLink")}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-1">
        <div className="flex min-w-0 gap-2">
          <input
            readOnly
            value={link.url ?? t("share.linkUnreadable")}
            aria-label={t("share.publicLink")}
            onFocus={(event) => event.target.select()}
            className="h-control min-w-0 flex-1 rounded-sm border border-outline bg-surface-container px-3 text-on-surface-variant outline-none focus:outline-2 focus:outline-offset-2 focus:outline-focus-ring pointer-coarse:h-touch"
          />
          <Button icon="content_copy" onClick={onCopy} disabled={!link.url}>
            {t("share.copy")}
          </Button>
        </div>
        <p className="m-0 px-1 text-body-sm text-on-surface-variant">
          {link.opens === 0 || !link.lastOpenedAt
            ? t("share.neverOpened")
            : t("share.opened", {
                count: link.opens,
                when: format.date(link.lastOpenedAt, { inSentence: true }),
              })}
        </p>
      </div>

      <div className="flex flex-col divide-y divide-outline-variant rounded-md border border-outline-variant bg-surface-container-lowest">
        <div className="flex flex-col">
          {row(
            "event",
            t("share.expires"),
            t("share.expiresHint"),
            <Switch checked={expires} onCheckedChange={setExpires} label={t("share.expires")} />,
          )}
          {expires && (
            <TextField
              type="date"
              label={t("share.lastDay")}
              value={day}
              min={isoDay(tomorrow)}
              onChange={(event) => setDay(event.target.value)}
              className="px-4 pb-3 pl-13"
            />
          )}
        </div>
        <div className="flex flex-col">
          {row(
            "password",
            t("share.password"),
            t("share.passwordHint"),
            <Switch checked={protect} onCheckedChange={setProtect} label={t("share.password")} />,
          )}
          {protect &&
            (changing ? (
              <TextField
                type="password"
                label={t("share.newPassword")}
                value={password}
                autoComplete="new-password"
                onChange={(event) => {
                  setPassword(event.target.value);
                  setMissing(false);
                }}
                error={missing ? t("share.passwordMissing") : undefined}
                className="px-4 pb-3 pl-13"
              />
            ) : (
              <div className="flex items-center gap-2 px-4 pb-3 pl-13">
                <span className="flex-1 text-body-md">{t("share.passwordSet")}</span>
                <Button variant="text" onClick={() => setChanging(true)}>
                  {t("share.changePassword")}
                </Button>
              </div>
            ))}
        </div>
        {row(
          "download",
          t("share.allowDownload"),
          t("share.allowDownloadHint"),
          <Switch checked={allowDownload} onCheckedChange={setAllowDownload} label={t("share.allowDownload")} />,
        )}
      </div>

      <p className="m-0 text-body-sm text-on-surface-variant">
        {isFolder ? t("share.linkSeesFolder") : t("share.linkSeesFile")}
      </p>
      {error && (
        <p role="alert" className="m-0 flex items-center gap-1 text-body-sm text-error">
          <Icon name="error" size={16} />
          {error}
        </p>
      )}
    </Dialog>
  );
}
