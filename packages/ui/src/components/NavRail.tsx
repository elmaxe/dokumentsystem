import { useTranslation } from "react-i18next";
import { cn } from "../lib/cn";
import { places, type Place } from "../lib/places";
import { Icon } from "./Icon";
import { ActionMenu, type MenuAction } from "./Menu";
import { StorageMeter } from "./StorageMeter";

export type NavRailProps = {
  active?: Place;
  onNavigate: (place: Place) => void;
  /** The New menu: new folder, upload files. */
  newActions?: MenuAction[];
  onNewAction?: (id: string) => void;
  used: number;
  /** Null means no limit. */
  quota: number | null;
  /** What emptying the trash would free, in bytes. */
  trash?: number;
  /** A count on a destination, such as new items in Shared with me. */
  badges?: Partial<Record<Place, number>>;
  /** auto collapses to 88px under 1200px and expands to 256px with the storage meter above (§16.3). */
  mode?: "auto" | "expanded" | "collapsed";
  className?: string;
};

export function NavRail({
  active,
  onNavigate,
  newActions = [],
  onNewAction = () => {},
  used,
  quota,
  trash,
  badges,
  mode = "auto",
  className,
}: NavRailProps) {
  const { t } = useTranslation();
  return (
    <nav
      aria-label={t("nav.label")}
      data-rail={mode}
      className={cn(
        "flex w-rail shrink-0 flex-col items-center gap-1 bg-surface-container-low px-3 pt-3 pb-2 rail-expanded:w-nav-drawer rail-expanded:items-stretch",
        className,
      )}
    >
      <div className="hidden items-center gap-2 px-3 pt-2 pb-4 text-[18px] leading-6 font-semibold tracking-tight text-on-surface rail-expanded:flex">
        <Icon name="folder_open" filled className="text-primary" />
        {t("app.name")}
      </div>
      <ActionMenu
        align="start"
        actions={newActions}
        onAction={onNewAction}
        trigger={
        <button
          type="button"
          aria-label={t("files.new")}
          className="mb-3 inline-flex size-14 cursor-pointer items-center justify-center gap-3 self-center rounded-md bg-secondary-container text-label-lg text-on-secondary-container transition-shadow duration-(--duration-short) hover:shadow-elevation-1 rail-expanded:w-auto rail-expanded:self-start rail-expanded:pr-5 rail-expanded:pl-4"
        >
          <Icon name="add" />
          <span className="hidden rail-expanded:inline">{t("files.new")}</span>
        </button>
        }
      />
      {places.map((place) => (
        <RailItem
          key={place.id}
          icon={place.icon}
          label={t(place.key)}
          active={place.id === active}
          badge={badges?.[place.id]}
          onClick={() => onNavigate(place.id)}
        />
      ))}
      <div className="min-h-4 flex-1" />
      <StorageMeter
        used={used}
        quota={quota}
        trash={trash}
        className="hidden border-t border-outline-variant px-4 pt-3 pb-2 rail-expanded:flex"
      />
    </nav>
  );
}

/** One destination in a rail: icon over label at 88px, icon beside label when expanded. */
export function RailItem({
  icon,
  label,
  active,
  badge,
  onClick,
}: {
  icon: string;
  label: string;
  active: boolean;
  badge?: number;
  onClick: () => void;
}) {
  const { t } = useTranslation();
  return (
    <button
      type="button"
      onClick={onClick}
      aria-current={active ? "page" : undefined}
      className={cn(
        "group flex w-full cursor-pointer flex-col items-center gap-1 pb-2 text-label-md rail-expanded:h-10 rail-expanded:flex-row rail-expanded:gap-3 rail-expanded:rounded-xl rail-expanded:px-4 rail-expanded:pb-0 rail-expanded:text-body-md rail-expanded:font-medium",
        active
          ? "text-on-surface rail-expanded:bg-secondary-container rail-expanded:font-semibold rail-expanded:text-on-secondary-container"
          : "text-on-surface-variant hover:text-on-surface rail-expanded:hover:bg-on-surface/8",
      )}
    >
      <span
        className={cn(
          "flex h-8 w-14 items-center justify-center rounded-full rail-expanded:h-auto rail-expanded:w-auto rail-expanded:bg-transparent",
          active ? "bg-secondary-container text-on-secondary-container" : "group-hover:bg-on-surface/8 rail-expanded:group-hover:bg-transparent",
        )}
      >
        <span className="relative inline-flex">
          <Icon name={icon} filled={active} />
          {!!badge && (
            <span className="absolute -top-1 -right-2 inline-flex h-4 min-w-4 items-center justify-center rounded-full bg-error px-1 text-[11px] leading-none font-medium text-on-error rail-expanded:hidden">
              {badge}
            </span>
          )}
        </span>
      </span>
      <span className="max-w-full text-center leading-4 rail-expanded:flex-1 rail-expanded:truncate rail-expanded:text-left rail-expanded:leading-5">
        {label}
      </span>
      {!!badge && (
        <span className="hidden min-w-5 rounded-full bg-primary px-1.5 text-center text-label-sm text-on-primary rail-expanded:inline">
          {badge}
        </span>
      )}
      {!!badge && <span className="sr-only">{t("nav.newCount", { count: badge })}</span>}
    </button>
  );
}
