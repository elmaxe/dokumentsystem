import { useState } from "react";
import { useTranslation } from "react-i18next";
import { dayLabel, lastDay } from "../lib/links";
import { Avatar, type AvatarTone } from "./Avatar";
import { Button } from "./Button";
import { Dialog, type DialogVariant } from "./Dialog";
import { Icon } from "./Icon";
import { ActionMenu } from "./Menu";
import { Switch } from "./Switch";

export type ShareRole = "owner" | "editor" | "viewer";

/** A role a share can give; the owner's is implicit. */
export type GrantRole = Exclude<ShareRole, "owner">;

export type SharePerson = {
  /** The person's id; also the React key, falling back to the email. */
  id?: string;
  name: string;
  email: string;
  role: ShareRole;
  you?: boolean;
  /** The folder this access comes from, when it comes from a folder above (§9). */
  inheritedFrom?: string;
  /** What that folder gives: the role can't go below it here. */
  inheritedRole?: GrantRole;
  /** Whether there is a share on this item itself, so "Remove access" applies. */
  removable?: boolean;
  tone?: AvatarTone;
};

/** Someone the person can share with, offered as they type. */
export type ShareCandidate = { id: string; name: string; email?: string | null };

/** A node's public link (§9), as the share dialog sums it up. */
export type ShareLink = {
  /** When it stops working, ISO 8601; null for never. */
  expiresAt?: string | null;
  hasPassword: boolean;
  allowDownload: boolean;
};

export type ShareDialogProps = {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  name: string;
  /** The owner first, then everyone with access. */
  people: SharePerson[];
  /** False for people it's shared with: they see who has access but can't change it. */
  canManage?: boolean;
  /** Who matches what's typed in "Add people". */
  candidates?: ShareCandidate[];
  onQueryChange?: (query: string) => void;
  onAdd: (candidate: ShareCandidate, role: GrantRole) => void;
  /** A new role for someone, or "remove" to take away this item's share. */
  onRoleChange: (person: SharePerson, role: GrantRole | "remove") => void;
  /** Why the last change was refused, shown under the field. */
  error?: string;
  /**
   * The public link section, for the owner: the link when it's on, null when it's off. Left out, the section isn't
   * shown, as for people it's shared with.
   */
  link?: ShareLink | null;
  onLinkToggle?: (on: boolean) => void;
  onCopyLink?: () => void;
  onLinkSettings?: () => void;
  variant?: DialogVariant;
};

const grantRoles: GrantRole[] = ["viewer", "editor"];
const rank: Record<GrantRole, number> = { viewer: 1, editor: 2 };

/**
 * Add people, then people with access, then the public link with expiry, password and download options (§9, §16.4).
 * Roles from a folder above are labelled with the folder and can only be raised here. A bottom sheet on phones.
 */
export function ShareDialog({
  open,
  onOpenChange,
  name,
  people,
  canManage = true,
  candidates = [],
  onQueryChange,
  onAdd,
  onRoleChange,
  error,
  link,
  onLinkToggle,
  onCopyLink,
  onLinkSettings,
  variant,
}: ShareDialogProps) {
  const { t, i18n } = useTranslation();
  const [draft, setDraft] = useState("");
  const [role, setRole] = useState<GrantRole>("viewer");
  const already = new Set(people.map((p) => p.id ?? p.email));
  const offered = draft.trim() ? candidates.filter((c) => !already.has(c.id)).slice(0, 6) : [];
  const owner = people.find((p) => p.role === "owner");

  const type = (value: string) => {
    setDraft(value);
    onQueryChange?.(value.trim());
  };
  const add = (candidate: ShareCandidate) => {
    onAdd(candidate, role);
    type("");
  };

  return (
    <Dialog
      open={open}
      onOpenChange={onOpenChange}
      variant={variant}
      title={t("share.title", { name })}
      footer={<Button onClick={() => onOpenChange(false)}>{t("common.done")}</Button>}
    >
      {canManage ? (
        <div className="flex flex-col gap-1">
          <div className="flex gap-2">
            <form
              onSubmit={(event) => {
                event.preventDefault();
                if (offered[0]) add(offered[0]);
              }}
              className="flex h-control min-w-0 flex-1 items-center gap-2 rounded-sm border border-outline bg-surface-container-lowest px-3 text-on-surface focus-within:outline-2 focus-within:outline-offset-2 focus-within:outline-focus-ring pointer-coarse:h-touch"
            >
              <Icon name="person_add" className="text-on-surface-variant" />
              <input
                value={draft}
                onChange={(event) => type(event.target.value)}
                placeholder={t("share.addPeople")}
                aria-label={t("share.addPeople")}
                aria-autocomplete="list"
                className="min-w-0 flex-1 bg-transparent outline-none placeholder:text-on-surface-variant"
              />
            </form>
            <ActionMenu
              onAction={setRole}
              actions={grantRoles.map((id) => ({
                id,
                icon: id === role ? "check" : "",
                label: t(`share.roles.${id}`),
              }))}
              trigger={
                <button
                  type="button"
                  aria-label={t("share.newRole", { role: t(`share.roles.${role}`) })}
                  className="inline-flex h-control shrink-0 cursor-pointer items-center gap-0.5 rounded-sm border border-outline bg-surface-container-lowest pr-1 pl-3 text-on-surface hover:bg-on-surface/8 pointer-coarse:h-touch"
                >
                  {t(`share.roles.${role}`)}
                  <Icon name="arrow_drop_down" />
                </button>
              }
            />
          </div>
          {offered.length > 0 && (
            <ul role="listbox" aria-label={t("share.matches")} className="m-0 flex list-none flex-col rounded-sm bg-surface-container-lowest p-0 py-1 shadow-elevation-1">
              {offered.map((candidate) => (
                <li key={candidate.id} role="option" aria-selected={false}>
                  <button
                    type="button"
                    onClick={() => add(candidate)}
                    className="flex w-full cursor-pointer items-center gap-3 px-3 py-2 text-left hover:bg-on-surface/8"
                  >
                    <Avatar name={candidate.name} size="sm" />
                    <span className="flex min-w-0 flex-col">
                      <span className="truncate text-on-surface">{candidate.name}</span>
                      {candidate.email && <span className="truncate text-body-sm">{candidate.email}</span>}
                    </span>
                  </button>
                </li>
              ))}
            </ul>
          )}
          {draft.trim() && offered.length === 0 && (
            <p className="m-0 px-1 text-body-sm text-on-surface-variant">{t("share.noMatches")}</p>
          )}
          {error && (
            <p role="alert" className="m-0 flex items-center gap-1 px-1 text-body-sm text-error">
              <Icon name="error" size={16} />
              {error}
            </p>
          )}
        </div>
      ) : (
        owner && (
          <p className="m-0 text-body-md text-on-surface-variant">{t("share.onlyOwner", { name: owner.name })}</p>
        )
      )}

      <section>
        <h3 className="m-0 mb-2 text-title-sm text-on-surface">{t("share.peopleWithAccess")}</h3>
        <ul className="m-0 flex list-none flex-col p-0">
          {people.map((person) => (
            <li key={person.id ?? person.email} className="flex min-w-0 items-center gap-3 py-2">
              <Avatar name={person.name} tone={person.tone} />
              <div className="flex min-w-0 flex-1 flex-col">
                <span className="truncate font-medium text-on-surface">
                  {person.you ? t("share.you", { name: person.name }) : person.name}
                </span>
                {person.inheritedFrom && person.inheritedRole ? (
                  <span className="flex items-center gap-1 truncate text-body-sm">
                    <Icon name="subdirectory_arrow_right" size={16} />
                    {t(`share.inherited.${person.inheritedRole}`, { folder: person.inheritedFrom })}
                  </span>
                ) : (
                  <span className="truncate text-body-sm">{person.email}</span>
                )}
              </div>
              {person.role === "owner" || !canManage ? (
                <span className="px-2 text-body-md">{t(`share.roles.${person.role}`)}</span>
              ) : (
                <ActionMenu
                  onAction={(id) => onRoleChange(person, id)}
                  actions={[
                    ...grantRoles.map((id) => ({
                      id,
                      icon: id === person.role ? "check" : "",
                      label: t(`share.roles.${id}`),
                      disabled: !!person.inheritedRole && rank[id] < rank[person.inheritedRole],
                    })),
                    ...(person.removable
                      ? [
                          {
                            id: "remove" as const,
                            icon: "person_remove",
                            label: t("share.removeAccess"),
                            danger: true,
                            separatorBefore: true,
                          },
                        ]
                      : []),
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
        {canManage && <p className="m-0 mt-1 text-body-sm text-on-surface-variant">{t("share.rolesHint")}</p>}
      </section>

      {link !== undefined && onLinkToggle && (
        <section>
          <h3 className="m-0 mb-2 text-title-sm text-on-surface">{t("share.publicLink")}</h3>
          <div className="flex flex-col gap-3 rounded-md border border-outline-variant bg-surface-container-lowest px-4 py-3">
            <div className="flex min-w-0 items-center gap-3">
              <Avatar icon={link ? "link" : "lock"} tone={link ? 1 : 0} />
              <div className="flex min-w-0 flex-1 flex-col">
                <span className="font-medium text-on-surface">{link ? t("share.linkOn") : t("share.linkOff")}</span>
                <span className="text-body-sm">
                  {link
                    ? [
                        link.expiresAt
                          ? t("share.linkUntil", { date: dayLabel(lastDay(link.expiresAt), i18n.language) })
                          : t("share.noExpiry"),
                        link.allowDownload ? t("share.downloadAllowed") : t("share.viewOnly"),
                        ...(link.hasPassword ? [t("share.passwordSet")] : []),
                      ].join(" · ")
                    : t("share.linkOffHint")}
                </span>
              </div>
              <Switch checked={!!link} onCheckedChange={onLinkToggle} label={t("share.publicLink")} />
            </div>
            {link && (
              <div className="flex flex-wrap gap-2">
                {onCopyLink && (
                  <Button variant="outlined" icon="content_copy" onClick={onCopyLink}>
                    {t("share.copyLink")}
                  </Button>
                )}
                {onLinkSettings && (
                  <Button variant="text" icon="tune" onClick={onLinkSettings}>
                    {t("share.linkSettings")}
                  </Button>
                )}
              </div>
            )}
          </div>
        </section>
      )}
    </Dialog>
  );
}
