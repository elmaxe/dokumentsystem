import { useTranslation } from "react-i18next";
import { cn } from "../lib/cn";
import { adminPlaces, type AdminPlace } from "../lib/places";
import { BottomNavItem } from "./BottomNav";
import { Icon } from "./Icon";
import { RailItem } from "./NavRail";

export type AdminRailProps = {
  active?: AdminPlace;
  onNavigate: (place: AdminPlace) => void;
  /** Leaves admin for My files. */
  onExit: () => void;
  mode?: "auto" | "expanded" | "collapsed";
  className?: string;
};

/** Takes the nav rail's place on admin pages: a way back to the files, then the admin pages (§16.3). */
export function AdminRail({ active, onNavigate, onExit, mode = "auto", className }: AdminRailProps) {
  const { t } = useTranslation();
  return (
    <nav
      aria-label={t("admin.nav.label")}
      data-rail={mode}
      className={cn(
        "flex w-rail shrink-0 flex-col items-center gap-1 bg-surface-container-low px-3 pt-3 pb-2 rail-expanded:w-nav-drawer rail-expanded:items-stretch",
        className,
      )}
    >
      <div className="hidden items-center gap-2 px-3 pt-2 pb-4 text-[18px] leading-6 font-semibold tracking-tight text-on-surface rail-expanded:flex">
        <Icon name="admin_panel_settings" filled className="text-primary" />
        {t("admin.nav.title")}
      </div>
      <button
        type="button"
        onClick={onExit}
        aria-label={t("admin.nav.exit")}
        title={t("admin.nav.exit")}
        className="mb-3 inline-flex size-14 cursor-pointer items-center justify-center gap-3 self-center rounded-md bg-secondary-container text-label-lg text-on-secondary-container transition-shadow duration-(--duration-short) hover:shadow-elevation-1 rail-expanded:w-auto rail-expanded:self-start rail-expanded:pr-5 rail-expanded:pl-4"
      >
        <Icon name="arrow_back" />
        <span className="hidden rail-expanded:inline">{t("admin.nav.exit")}</span>
      </button>
      {adminPlaces.map((place) => (
        <RailItem
          key={place.id}
          icon={place.icon}
          label={t(place.key)}
          active={place.id === active}
          onClick={() => onNavigate(place.id)}
        />
      ))}
    </nav>
  );
}

export type AdminBottomNavProps = {
  active?: AdminPlace;
  onNavigate: (place: AdminPlace) => void;
  className?: string;
};

/** The bottom bar on admin pages under 840px. The way back to the files is in the top bar. */
export function AdminBottomNav({ active, onNavigate, className }: AdminBottomNavProps) {
  const { t } = useTranslation();
  return (
    <nav aria-label={t("admin.nav.label")} className={cn("flex h-20 w-full bg-surface-container pt-3 pb-4", className)}>
      {adminPlaces.map((place) => (
        <BottomNavItem
          key={place.id}
          icon={place.icon}
          label={t(place.key)}
          active={place.id === active}
          onClick={() => onNavigate(place.id)}
        />
      ))}
    </nav>
  );
}
