import { useState } from "react";
import { useTranslation } from "react-i18next";
import { cn } from "../lib/cn";
import { Avatar, type AvatarTone } from "./Avatar";
import { Button, IconButton } from "./Button";
import { Chip } from "./Chip";
import { Dialog, type DialogVariant } from "./Dialog";
import { Icon } from "./Icon";
import { ActionMenu } from "./Menu";
import { Switch } from "./Switch";

export type ShareRole = "owner" | "editor" | "viewer";

export type SharePerson = {
  name: string;
  email: string;
  role: ShareRole;
  you?: boolean;
  /** The folder this access comes from. Inherited grants are labelled and cannot be lowered here. */
  inheritedFrom?: string;
  tone?: AvatarTone;
};

export type ShareLink = {
  enabled: boolean;
  /** Days until it expires; undefined means no expiry. */
  expiresInDays?: number;
  password: boolean;
  allowDownload: boolean;
};

export type ShareDialogProps = {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  name: string;
  people: SharePerson[];
  onPeopleChange: (people: SharePerson[]) => void;
  link: ShareLink;
  onLinkChange: (link: ShareLink) => void;
  onCopyLink: () => void;
  variant?: DialogVariant;
};

/**
 * Add people, then people with access, then the public link with expiry, password and download
 * options (§16.4). A bottom sheet on phones.
 */
export function ShareDialog({
  open,
  onOpenChange,
  name,
  people,
  onPeopleChange,
  link,
  onLinkChange,
  onCopyLink,
  variant,
}: ShareDialogProps) {
  const { t } = useTranslation();
  const [draft, setDraft] = useState("");

  const add = () => {
    const email = draft.trim();
    if (!email) return;
    const person: SharePerson = {
      name: email.split("@")[0] ?? email,
      email,
      role: "viewer",
      tone: ((people.length % 3) + 1) as AvatarTone,
    };
    onPeopleChange([...people, person]);
    setDraft("");
  };

  const setRole = (email: string, role: ShareRole | "remove") =>
    onPeopleChange(
      role === "remove"
        ? people.filter((p) => p.email !== email)
        : people.map((p) => (p.email === email ? { ...p, role } : p)),
    );

  return (
    <Dialog
      open={open}
      onOpenChange={onOpenChange}
      variant={variant}
      title={t("share.title", { name })}
      headAction={<IconButton icon="help" label={t("share.help")} size="sm" />}
      footer={
        <>
          <Button variant="outlined" icon="link" onClick={onCopyLink} disabled={!link.enabled}>
            {t("share.copyLink")}
          </Button>
          <Button onClick={() => onOpenChange(false)}>{t("common.done")}</Button>
        </>
      }
    >
      <form
        onSubmit={(event) => {
          event.preventDefault();
          add();
        }}
        className="flex h-control items-center gap-2 rounded-sm border border-outline bg-surface-container-lowest px-3 text-on-surface focus-within:outline-2 focus-within:outline-offset-2 focus-within:outline-focus-ring pointer-coarse:h-touch"
      >
        <Icon name="person_add" className="text-on-surface-variant" />
        <input
          value={draft}
          onChange={(event) => setDraft(event.target.value)}
          placeholder={t("share.addPeople")}
          aria-label={t("share.addPeople")}
          className="min-w-0 flex-1 bg-transparent outline-none placeholder:text-on-surface-variant"
        />
      </form>

      <section>
        <h3 className="m-0 mb-2 text-title-sm text-on-surface">{t("share.peopleWithAccess")}</h3>
        <ul className="m-0 flex list-none flex-col p-0">
          {people.map((person) => (
            <li key={person.email} className="flex min-w-0 items-center gap-3 py-2">
              <Avatar name={person.name} tone={person.tone} />
              <div className="flex min-w-0 flex-1 flex-col">
                <span className="truncate font-medium text-on-surface">
                  {person.you ? t("share.you", { name: person.name }) : person.name}
                </span>
                {person.inheritedFrom ? (
                  <span className="flex items-center gap-1 truncate text-body-sm">
                    <Icon name="subdirectory_arrow_right" size={16} />
                    {t("share.inheritedFrom", { folder: person.inheritedFrom })}
                  </span>
                ) : (
                  <span className="truncate text-body-sm">{person.email}</span>
                )}
              </div>
              {person.role === "owner" || person.inheritedFrom ? (
                <span className="px-2 text-body-md">{t(`share.roles.${person.role}`)}</span>
              ) : (
                <ActionMenu
                  onAction={(role) => setRole(person.email, role)}
                  actions={[
                    { id: "editor", icon: "edit", label: t("share.roles.editor") },
                    { id: "viewer", icon: "visibility", label: t("share.roles.viewer") },
                    {
                      id: "remove",
                      icon: "person_remove",
                      label: t("share.removeAccess"),
                      danger: true,
                      separatorBefore: true,
                    },
                  ]}
                  trigger={
                    <button
                      type="button"
                      aria-label={t("share.changeRole", { name: person.name })}
                      className="inline-flex h-8 cursor-pointer items-center gap-0.5 rounded-sm pr-1 pl-2 font-medium whitespace-nowrap text-on-surface hover:bg-on-surface/8"
                    >
                      {t(`share.roles.${person.role}`)}
                      <Icon name="arrow_drop_down" />
                    </button>
                  }
                />
              )}
            </li>
          ))}
        </ul>
      </section>

      <section>
        <h3 className="m-0 mb-2 text-title-sm text-on-surface">{t("share.publicLink")}</h3>
        <div className="flex flex-col gap-3 rounded-md border border-outline-variant bg-surface-container-lowest px-4 py-3">
          <div className="flex min-w-0 items-center gap-3">
            <Avatar icon={link.enabled ? "public" : "lock"} tone={link.enabled ? 1 : 0} />
            <div className="flex min-w-0 flex-1 flex-col">
              <span className="font-medium text-on-surface">
                {link.enabled ? t("share.linkOn") : t("share.linkOff")}
              </span>
              <span className="text-body-sm">
                {link.enabled ? t("share.linkOnHint") : t("share.linkOffHint")}
              </span>
            </div>
            <Switch
              checked={link.enabled}
              onCheckedChange={(enabled) => onLinkChange({ ...link, enabled })}
              label={t("share.publicLink")}
            />
          </div>
          <div className={cn("flex flex-wrap gap-2", !link.enabled && "hidden")}>
            <Chip
              icon="event"
              selected={link.expiresInDays !== undefined}
              onClick={() =>
                onLinkChange({
                  ...link,
                  expiresInDays: link.expiresInDays === undefined ? 7 : undefined,
                })
              }
            >
              {link.expiresInDays === undefined
                ? t("share.noExpiry")
                : t("share.expiresIn", { count: link.expiresInDays })}
            </Chip>
            <Chip
              icon="password"
              selected={link.password}
              onClick={() => onLinkChange({ ...link, password: !link.password })}
            >
              {link.password ? t("share.passwordSet") : t("share.addPassword")}
            </Chip>
            <Chip
              icon={link.allowDownload ? "download" : "file_download_off"}
              selected={link.allowDownload}
              onClick={() => onLinkChange({ ...link, allowDownload: !link.allowDownload })}
            >
              {link.allowDownload ? t("share.downloadAllowed") : t("share.viewOnly")}
            </Chip>
          </div>
        </div>
      </section>
    </Dialog>
  );
}
