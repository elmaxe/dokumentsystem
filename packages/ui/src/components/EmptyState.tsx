import type { ReactNode } from "react";
import { cn } from "../lib/cn";
import { Icon } from "./Icon";

export type EmptyStateProps = {
  title: string;
  text?: ReactNode;
  icon?: string;
  /** The drag-and-drop target: names the destination folder in the title (§16.4). */
  dropping?: boolean;
  action?: ReactNode;
  className?: string;
};

export function EmptyState({ title, text, icon, dropping, action, className }: EmptyStateProps) {
  return (
    <div
      className={cn(
        "flex flex-col items-center gap-3 rounded-lg border-2 border-dashed px-6 py-12 text-center",
        dropping
          ? "border-primary bg-primary-container text-on-primary-container"
          : "border-outline-variant text-on-surface-variant",
        className,
      )}
    >
      <Icon
        name={icon ?? (dropping ? "upload" : "folder_open")}
        filled
        size={48}
        className={dropping ? "text-on-primary-container" : "text-primary"}
      />
      <h2
        className={cn(
          "m-0 text-headline-sm",
          dropping ? "text-on-primary-container" : "text-on-surface",
        )}
      >
        {title}
      </h2>
      {text && <p className="m-0 max-w-90">{text}</p>}
      {action}
    </div>
  );
}
