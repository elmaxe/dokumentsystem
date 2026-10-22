import { cn } from "../lib/cn";

export type ProgressProps = {
  /** 0–100. Leave it out for an indeterminate bar (hashing, waiting). */
  value?: number;
  tone?: "primary" | "paused" | "warning" | "error";
  label: string;
  className?: string;
};

const fills = {
  primary: "bg-primary",
  paused: "bg-on-surface-variant",
  warning: "bg-warning",
  error: "bg-error",
};

export function Progress({ value, tone = "primary", label, className }: ProgressProps) {
  return (
    <div
      role="progressbar"
      aria-label={label}
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={value}
      className={cn("h-1 overflow-hidden rounded-full bg-surface-container-highest", className)}
    >
      <span
        className={cn(
          "block h-full rounded-full transition-[width] duration-(--duration-medium)",
          fills[tone],
          value === undefined && "w-[30%] animate-indeterminate",
        )}
        style={value === undefined ? undefined : { width: `${value}%` }}
      />
    </div>
  );
}
