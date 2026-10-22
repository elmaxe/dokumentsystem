import { formatBytes, formatFileDate } from "@dokument/core";
import { Tabs } from "radix-ui";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { cn } from "../lib/cn";
import { isFolder, type FileItem } from "../lib/files";
import { Avatar, type AvatarTone } from "./Avatar";
import { Button, IconButton } from "./Button";
import { FileTypeIcon } from "./FileTypeIcon";
import type { SharePerson } from "./ShareDialog";
import { VersionHistory, type VersionHistoryProps } from "./VersionHistory";

export type DetailsTab = "info" | "sharing" | "versions" | "activity";

export type ActivityEntry = { id: string; who: string; tone?: AvatarTone; what: string; at: string };

export type FileDetails = {
  location: string;
  created: string;
  /** The content hash, shown in mono (§16.2). */
  checksum?: string;
  people: SharePerson[];
  publicLink?: boolean;
  activity: ActivityEntry[];
};

export type DetailsPanelProps = {
  item: FileItem;
  details: FileDetails;
  tab: DetailsTab;
  onTabChange: (tab: DetailsTab) => void;
  onClose: () => void;
  onManageAccess?: () => void;
  versions?: VersionHistoryProps;
  className?: string;
};

function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-0.5 py-2">
      <dt className="text-body-sm text-on-surface-variant">{label}</dt>
      <dd className="m-0 text-body-md break-words text-on-surface">{children}</dd>
    </div>
  );
}

/** Info, sharing, versions and activity for one file, beside the file area at 1200px and up (§15.2). */
export function DetailsPanel({
  item,
  details,
  tab,
  onTabChange,
  onClose,
  onManageAccess,
  versions,
  className,
}: DetailsPanelProps) {
  const { t, i18n } = useTranslation();
  const folder = isFolder(item);
  const tabs: DetailsTab[] = folder
    ? ["info", "sharing", "activity"]
    : ["info", "sharing", "versions", "activity"];
  const date = (iso: string) => formatFileDate(iso, i18n.language);

  return (
    <aside
      aria-label={t("details.label", { name: item.name })}
      className={cn(
        "flex w-details max-w-full shrink-0 flex-col overflow-hidden bg-surface-container-low",
        className,
      )}
    >
      <div className="flex items-center gap-3 py-3 pr-2 pl-4">
        <FileTypeIcon kind={item.kind} shared={item.shared} />
        <h2 title={item.name} className="m-0 min-w-0 flex-1 truncate text-title-md">
          {item.name}
        </h2>
        <IconButton icon="close" label={t("details.close")} onClick={onClose} />
      </div>
      <Tabs.Root
        value={tabs.includes(tab) ? tab : "info"}
        onValueChange={(value) => onTabChange(value as DetailsTab)}
        className="flex min-h-0 flex-1 flex-col"
      >
        <Tabs.List className="flex gap-1 border-b border-outline-variant px-4">
          {tabs.map((id) => (
            <Tabs.Trigger
              key={id}
              value={id}
              className="cursor-pointer border-b-3 border-transparent px-2 py-3 text-label-lg text-on-surface-variant hover:text-on-surface data-[state=active]:border-primary data-[state=active]:text-primary"
            >
              {t(`details.tabs.${id}`)}
            </Tabs.Trigger>
          ))}
        </Tabs.List>
        <div className="min-h-0 flex-1 overflow-y-auto px-4 pb-4">
          <Tabs.Content value="info" className="outline-none">
            {!folder && (
              <div className="my-4 flex aspect-4/3 items-center justify-center overflow-hidden rounded-sm bg-surface-container-high">
                {item.thumbnail ? (
                  <img src={item.thumbnail} alt="" className="size-full object-cover" />
                ) : (
                  <FileTypeIcon kind={item.kind} size={56} />
                )}
              </div>
            )}
            <dl className="m-0 flex flex-col">
              <Row label={t("details.type")}>{t(`kinds.${item.kind}`)}</Row>
              {item.size !== undefined && (
                <Row label={t("details.size")}>
                  <span className="tabular">{formatBytes(item.size, i18n.language)}</span>
                </Row>
              )}
              <Row label={t("details.location")}>{details.location}</Row>
              <Row label={t("details.owner")}>{item.owner}</Row>
              <Row label={t("details.modified")}>{date(item.modified)}</Row>
              <Row label={t("details.created")}>{date(details.created)}</Row>
              {details.checksum && (
                <Row label={t("details.checksum")}>
                  <span className="font-mono text-mono-sm break-all">{details.checksum}</span>
                </Row>
              )}
            </dl>
          </Tabs.Content>
          <Tabs.Content value="sharing" className="flex flex-col gap-2 pt-3 outline-none">
            <p className="m-0 text-body-md text-on-surface-variant">
              {details.publicLink ? t("details.sharingWithLink") : t("details.sharingPrivate")}
            </p>
            <ul className="m-0 flex list-none flex-col p-0">
              {details.people.map((person) => (
                <li key={person.email} className="flex min-w-0 items-center gap-3 py-2">
                  <Avatar name={person.name} tone={person.tone} />
                  <span className="min-w-0 flex-1 truncate">
                    {person.you ? t("share.you", { name: person.name }) : person.name}
                  </span>
                  <span className="text-body-sm text-on-surface-variant">
                    {t(`share.roles.${person.role}`)}
                  </span>
                </li>
              ))}
            </ul>
            <Button variant="outlined" icon="person_add" onClick={onManageAccess} className="self-start">
              {t("details.manageAccess")}
            </Button>
          </Tabs.Content>
          {!folder && (
            <Tabs.Content value="versions" className="outline-none">
              <VersionHistory versions={[]} {...versions} />
            </Tabs.Content>
          )}
          <Tabs.Content value="activity" className="outline-none">
            <ol className="m-0 flex list-none flex-col p-0 pt-2">
              {details.activity.map((entry) => (
                <li key={entry.id} className="flex gap-3 py-2">
                  <Avatar name={entry.who} tone={entry.tone} size="sm" className="mt-0.5" />
                  <div className="flex min-w-0 flex-col">
                    <span className="text-body-md text-on-surface">{entry.what}</span>
                    <span className="tabular text-body-sm text-on-surface-variant">{date(entry.at)}</span>
                  </div>
                </li>
              ))}
            </ol>
          </Tabs.Content>
        </div>
      </Tabs.Root>
    </aside>
  );
}
