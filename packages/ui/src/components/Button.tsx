import type { ButtonHTMLAttributes } from "react";
import { cn } from "../lib/cn";
import { Icon } from "./Icon";

export type ButtonVariant = "filled" | "tonal" | "outlined" | "text" | "danger";

export type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & {
  /** Use at most one filled button per view (§16.4). */
  variant?: ButtonVariant;
  icon?: string;
};

const variants: Record<ButtonVariant, string> = {
  filled: "border-transparent bg-primary text-on-primary hover:shadow-elevation-1",
  tonal:
    "border-transparent bg-secondary-container text-on-secondary-container hover:shadow-elevation-1",
  outlined: "border-outline text-primary hover:bg-secondary-container",
  text: "border-transparent px-3 text-primary hover:bg-secondary-container",
  danger: "border-transparent bg-error text-on-error hover:shadow-elevation-1",
};

export function Button({ variant = "filled", icon, className, children, ...props }: ButtonProps) {
  return (
    <button
      type="button"
      {...props}
      className={cn(
        "inline-flex h-control shrink-0 cursor-pointer items-center justify-center gap-2 rounded-full border px-6 text-label-lg whitespace-nowrap transition-[background-color,box-shadow] duration-(--duration-short) pointer-coarse:h-touch",
        "disabled:cursor-default disabled:border-transparent disabled:bg-surface-container-highest disabled:text-on-surface-variant disabled:opacity-70 disabled:shadow-none",
        variants[variant],
        icon && variant !== "text" && "pl-4",
        className,
      )}
    >
      {icon && <Icon name={icon} size={18} />}
      {children}
    </button>
  );
}

export type IconButtonProps = Omit<ButtonHTMLAttributes<HTMLButtonElement>, "children"> & {
  icon: string;
  /** Every icon button has a label: it is the accessible name and the tooltip (§16.5). */
  label: string;
  /** Makes it a toggle; the icon fills when selected. */
  selected?: boolean;
  filled?: boolean;
  size?: "sm" | "md";
};

export function IconButton({
  icon,
  label,
  selected,
  filled,
  size = "md",
  className,
  ...props
}: IconButtonProps) {
  return (
    <button
      type="button"
      aria-label={label}
      title={label}
      aria-pressed={selected}
      {...props}
      className={cn(
        "inline-flex shrink-0 cursor-pointer items-center justify-center rounded-full transition-colors duration-(--duration-short) disabled:cursor-default disabled:opacity-40",
        size === "sm" ? "size-8 pointer-coarse:size-10" : "size-control pointer-coarse:size-touch",
        selected
          ? "bg-secondary-container text-on-secondary-container"
          : "text-on-surface-variant hover:bg-on-surface/8 hover:text-on-surface",
        className,
      )}
    >
      <Icon name={icon} filled={filled || selected} size={size === "sm" ? "sm" : "md"} />
    </button>
  );
}
