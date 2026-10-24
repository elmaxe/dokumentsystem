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
  className?: string;
};

/** Navigation under 840px and in the app: My files, Shared with me, Search, Recent (§16.3). */
export function BottomNav({ active, onNavigate, className }: BottomNavProps) {
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
  onClick,
}: {
  icon: string;
  label: string;
  active: boolean;
  onClick: () => void;
}) {
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
        <Icon name={icon} filled={active} />
      </span>
      <span className="max-w-full truncate">{label}</span>
    </button>
  );
}
