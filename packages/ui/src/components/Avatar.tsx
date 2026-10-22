import { cn } from "../lib/cn";
import { Icon } from "./Icon";

export type AvatarTone = 0 | 1 | 2 | 3;

const tones: Record<AvatarTone, string> = {
  0: "bg-secondary-container text-on-secondary-container",
  1: "bg-primary-container text-on-primary-container",
  2: "bg-warning-container text-on-surface",
  3: "bg-success-container text-on-surface",
};

export type AvatarProps = {
  name?: string;
  /** Shows an icon instead of initials, such as "public" for the public link row. */
  icon?: string;
  tone?: AvatarTone;
  size?: "sm" | "md";
  className?: string;
};

/** Initials in a tonal circle. Decorative: the name is always written next to it. */
export function Avatar({ name = "", icon, tone = 0, size = "md", className }: AvatarProps) {
  const initials = name
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0])
    .join("")
    .toUpperCase();
  return (
    <span
      aria-hidden
      className={cn(
        "inline-flex shrink-0 items-center justify-center rounded-full font-semibold",
        size === "sm" ? "size-6 text-[11px]" : "size-8 text-[13px]",
        icon && tone === 0 ? "bg-surface-container-highest text-on-surface-variant" : tones[tone],
        className,
      )}
    >
      {icon ? <Icon name={icon} size="sm" /> : initials}
    </span>
  );
}
