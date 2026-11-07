import { useTranslation } from "react-i18next";
import { cn } from "../lib/cn";
import { Icon } from "./Icon";

export type BottomPlace = "files" | "shared" | "search" | "recent";

const items: { id: BottomPlace; icon: string; key: string }[] = [
  { id: "files", icon: "folder", key: "nav.myFiles" },
  { id: "shared", icon: "folder_shared", key: "nav.sharedWithMe" },
  { id: "search", icon: "search", key: "nav.search" },
  { id: "recent", icon: "schedule", key: "nav.recent" },
];

export type BottomNavProps = {
  active?: BottomPlace;
  onNavigate: (place: BottomPlace) => void;
  /** A count on a destination, such as new items in Shared with me. */
  badges?: Partial<Record<BottomPlace, number>>;
  className?: string;
};

/** Navigation under 840px and in the app: My files, Shared with me, Search, Recent (§16.3). */
export function BottomNav({ active, onNavigate, badges, className }: BottomNavProps) {
  const { t } = useTranslation();
  return (
    <nav
      aria-label={t("nav.label")}
      className={cn("flex h-20 w-full bg-surface-container pt-3 pb-4", className)}
    >
      {items.map((item) => (
        <BottomNavItem
          key={item.id}
          icon={item.icon}
          label={t(item.key)}
          active={item.id === active}
          badge={badges?.[item.id]}
          onClick={() => onNavigate(item.id)}
        />
      ))}
    </nav>
  );
}

/** One destination in a bottom bar. */
export function BottomNavItem({
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
        "flex min-w-0 flex-1 cursor-pointer flex-col items-center gap-1 px-1 text-label-md",
        active ? "text-on-surface" : "text-on-surface-variant",
      )}
    >
      <span
        className={cn(
          "flex h-8 w-16 items-center justify-center rounded-full",
          active && "bg-secondary-container text-on-secondary-container",
        )}
      >
        <span className="relative inline-flex">
          <Icon name={icon} filled={active} />
          {!!badge && (
            <span className="absolute -top-1 -right-2 inline-flex h-4 min-w-4 items-center justify-center rounded-full bg-error px-1 text-[11px] leading-none font-medium text-on-error">
              {badge}
            </span>
          )}
        </span>
      </span>
      <span className="max-w-full truncate">{label}</span>
      {!!badge && <span className="sr-only">{t("nav.newCount", { count: badge })}</span>}
    </button>
  );
}
