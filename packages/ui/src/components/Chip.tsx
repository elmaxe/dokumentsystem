import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { cn } from "../lib/cn";
import { Icon } from "./Icon";

export type ChipProps = {
  selected?: boolean;
  icon?: string;
  /** Shows a remove button, for active search filters. */
  onRemove?: () => void;
  onClick?: () => void;
  children: ReactNode;
};

/** A filter or option chip. Search filters mirror the query operators: "Type: PDF" for type:pdf (§16.4). */
export function Chip({ selected, icon, onRemove, onClick, children }: ChipProps) {
  const { t } = useTranslation();
  return (
    <span
      className={cn(
        "inline-flex h-8 shrink-0 items-center rounded-sm border text-label-md whitespace-nowrap transition-colors duration-(--duration-short)",
        selected
          ? "border-transparent bg-secondary-container text-on-secondary-container"
          : "border-outline text-on-surface-variant hover:bg-on-surface/8",
      )}
    >
      <button
        type="button"
        aria-pressed={onClick ? !!selected : undefined}
        onClick={onClick}
        className={cn(
          "inline-flex h-full cursor-pointer items-center gap-1 rounded-sm pl-3",
          onRemove ? "pr-1" : "pr-3",
        )}
      >
        {(icon || selected) && <Icon name={icon ?? "check"} size={18} />}
        {children}
      </button>
      {onRemove && (
        <button
          type="button"
          onClick={onRemove}
          onPointerDown={(event) => event.stopPropagation()}
          aria-label={t("search.removeFilter")}
          title={t("search.removeFilter")}
          className="mr-1 inline-flex cursor-pointer rounded-full p-0.5 hover:bg-on-surface/8"
        >
          <Icon name="close" size={18} />
        </button>
      )}
    </span>
  );
}
