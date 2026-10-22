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
      {places.map((place) => {
        const on = place.id === active;
        return (
          <button
            key={place.id}
            type="button"
            onClick={() => onNavigate(place.id)}
            aria-current={on ? "page" : undefined}
            className={cn(
              "group flex w-full cursor-pointer flex-col items-center gap-1 pb-2 text-label-md rail-expanded:h-10 rail-expanded:flex-row rail-expanded:gap-3 rail-expanded:rounded-xl rail-expanded:px-4 rail-expanded:pb-0 rail-expanded:text-body-md rail-expanded:font-medium",
              on
                ? "text-on-surface rail-expanded:bg-secondary-container rail-expanded:font-semibold rail-expanded:text-on-secondary-container"
                : "text-on-surface-variant hover:text-on-surface rail-expanded:hover:bg-on-surface/8",
            )}
          >
            <span
              className={cn(
                "flex h-8 w-14 items-center justify-center rounded-full rail-expanded:h-auto rail-expanded:w-auto rail-expanded:bg-transparent",
                on ? "bg-secondary-container text-on-secondary-container" : "group-hover:bg-on-surface/8 rail-expanded:group-hover:bg-transparent",
              )}
            >
              <Icon name={place.icon} filled={on} />
            </span>
            <span className="max-w-full text-center leading-4 rail-expanded:truncate rail-expanded:text-left rail-expanded:leading-5">
              {t(place.key)}
            </span>
          </button>
        );
      })}
      <div className="min-h-4 flex-1" />
      <StorageMeter
        used={used}
        quota={quota}
        className="hidden border-t border-outline-variant px-4 pt-3 pb-2 rail-expanded:flex"
      />
    </nav>
  );
}
