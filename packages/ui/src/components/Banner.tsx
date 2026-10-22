import type { ReactNode } from "react";
import { cn } from "../lib/cn";
import { Icon } from "./Icon";

export type BannerProps = {
  tone?: "info" | "warning" | "error" | "success";
  /** Status is always said in words too, never by colour alone (§16.1). */
  children: ReactNode;
  action?: ReactNode;
  className?: string;
};

const styles = {
  info: { box: "bg-secondary-container text-on-secondary-container", icon: "info", iconColor: "" },
  warning: { box: "bg-warning-container text-on-surface", icon: "warning", iconColor: "text-warning" },
  error: { box: "bg-error-container text-on-error-container", icon: "error", iconColor: "" },
  success: {
    box: "bg-success-container text-on-surface",
    icon: "check_circle",
    iconColor: "text-success",
  },
};

export function Banner({ tone = "info", children, action, className }: BannerProps) {
  const style = styles[tone];
  return (
    <div
      role={tone === "error" ? "alert" : "status"}
      className={cn("flex items-start gap-3 rounded-md px-4 py-3", style.box, className)}
    >
      <Icon name={style.icon} filled className={style.iconColor} />
      <div className="min-w-0 flex-1 self-center">{children}</div>
      {action}
    </div>
  );
}
