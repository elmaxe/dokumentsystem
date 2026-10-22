import type { CSSProperties } from "react";
import { cn } from "../lib/cn";

export type IconProps = {
  /** A Material Symbols Rounded ligature name, such as "folder" or "upload". */
  name: string;
  /** Filled is for the active or selected state; outlined is the default (§16.5). */
  filled?: boolean;
  /** sm is 20px, md is 24px. A number sets the pixel size, for thumbnails and empty states. */
  size?: "sm" | "md" | number;
  /** Names the icon for screen readers. Leave it out when text next to the icon says the same. */
  label?: string;
  className?: string;
};

export function Icon({ name, filled, size = "md", label, className }: IconProps) {
  const px = size === "sm" ? 20 : size === "md" ? 24 : size;
  const style = {
    fontSize: size === "md" ? undefined : px,
    "--icon-opsz": Math.min(48, Math.max(20, px)),
  } as CSSProperties;
  return (
    <span
      className={cn("icon", filled && "icon-filled", className)}
      style={style}
      role={label ? "img" : undefined}
      aria-label={label}
      aria-hidden={label ? undefined : true}
    >
      {name}
    </span>
  );
}
