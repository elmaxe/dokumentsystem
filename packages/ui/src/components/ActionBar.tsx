import { useTranslation } from "react-i18next";
import { cn } from "../lib/cn";
import { IconButton } from "./Button";
import { ActionMenu, type MenuAction } from "./Menu";

export type ActionBarProps<Id extends string> = {
  count: number;
  /** Shown as icon buttons: Share, Download, Move, Trash (§16.4). */
  actions: MenuAction<Id>[];
  /** Everything else goes in the More menu. */
  moreActions?: MenuAction<Id>[];
  onAction: (id: Id) => void;
  onClear: () => void;
  /** Phones float the bar above the bottom nav, with a shadow. */
  floating?: boolean;
  className?: string;
};

/** The selection count and what you can do with the selected files. */
export function ActionBar<Id extends string>({
  count,
  actions,
  moreActions,
  onAction,
  onClear,
  floating,
  className,
}: ActionBarProps<Id>) {
  const { t } = useTranslation();
  return (
    <div
      role="toolbar"
      aria-label={t("selection.label")}
      className={cn(
        "flex h-12 min-w-0 items-center gap-1 rounded-xl bg-secondary-container px-1 text-on-secondary-container",
        floating && "h-14 shadow-elevation-1",
        className,
      )}
    >
      <IconButton
        icon="close"
        label={t("selection.clear")}
        onClick={onClear}
        className="text-inherit hover:bg-surface-container-lowest/60"
      />
      <span className="tabular mr-auto truncate px-1 text-title-sm" aria-live="polite">
        {t("selection.count", { count })}
      </span>
      {actions.map((action) => (
        <IconButton
          key={action.id}
          icon={action.icon}
          label={action.label}
          onClick={() => onAction(action.id)}
          className="text-inherit hover:bg-surface-container-lowest/60"
        />
      ))}
      {moreActions && moreActions.length > 0 && (
        <ActionMenu
          actions={moreActions}
          onAction={onAction}
          trigger={
            <IconButton
              icon="more_vert"
              label={t("common.more")}
              className="text-inherit hover:bg-surface-container-lowest/60"
            />
          }
        />
      )}
    </div>
  );
}
