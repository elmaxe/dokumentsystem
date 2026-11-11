import { initials } from "@dokument/core";
import { useState } from "react";
import { useTranslation } from "react-i18next";
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
  /** The person's profile picture (§5.1). Initials show under it until it loads, and instead of it when it can't. */
  picture?: string | null;
  /** Shows an icon instead of initials, such as "public" for the public link row. */
  icon?: string;
  tone?: AvatarTone;
  size?: "sm" | "md" | "lg";
  className?: string;
};

/** A person's picture, or their initials in a tonal circle. Decorative: the name is always written next to it. */
export function Avatar({ name = "", picture, icon, tone = 0, size = "md", className }: AvatarProps) {
  const { i18n } = useTranslation();
  const [failed, setFailed] = useState<string | null>(null);
  const showPicture = !icon && picture && failed !== picture;
  return (
    <span
      aria-hidden
      className={cn(
        "relative inline-flex shrink-0 items-center justify-center overflow-hidden rounded-full font-semibold",
        size === "sm" ? "size-6 text-[11px]" : size === "lg" ? "size-12 text-[18px]" : "size-8 text-[13px]",
        icon && tone === 0 ? "bg-surface-container-highest text-on-surface-variant" : tones[tone],
        className,
      )}
    >
      {icon ? <Icon name={icon} size="sm" /> : initials(name, i18n.language)}
      {showPicture && (
        <img
          src={picture}
          alt=""
          draggable={false}
          onError={() => setFailed(picture)}
          className="absolute inset-0 size-full object-cover"
        />
      )}
    </span>
  );
}
