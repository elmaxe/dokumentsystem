import { ContextMenu, DropdownMenu } from "radix-ui";
import type { ReactNode } from "react";
import { cn } from "../lib/cn";
import { Icon } from "./Icon";

export type MenuAction<Id extends string = string> = {
  id: Id;
  label: string;
  icon: string;
  /** Destructive actions are red and confirm before they run (§16.1). */
  danger?: boolean;
  separatorBefore?: boolean;
  disabled?: boolean;
};

const content =
  "z-50 min-w-56 overflow-hidden rounded-sm bg-surface-container py-2 text-on-surface shadow-elevation-2";
const item =
  "flex h-control cursor-pointer items-center gap-3 px-4 text-body-md outline-none select-none data-disabled:cursor-default data-disabled:opacity-40 data-highlighted:bg-on-surface/8 pointer-coarse:h-touch pointer-coarse:text-body-lg";
const separator = "my-2 h-px bg-outline-variant";

function Item({ action }: { action: MenuAction }) {
  return (
    <>
      <Icon
        name={action.icon}
        className={action.danger ? "text-error" : "text-on-surface-variant"}
      />
      <span className={cn("flex-1", action.danger && "text-error")}>{action.label}</span>
    </>
  );
}

export type ActionMenuProps<Id extends string> = {
  /** The button that opens the menu, usually an IconButton. */
  trigger: ReactNode;
  actions: MenuAction<Id>[];
  onAction: (id: Id) => void;
  align?: "start" | "end";
};

/** A dropdown of actions, opened from a button such as "More actions". */
export function ActionMenu<Id extends string>({
  trigger,
  actions,
  onAction,
  align = "end",
}: ActionMenuProps<Id>) {
  return (
    <DropdownMenu.Root modal={false}>
      <DropdownMenu.Trigger asChild>{trigger}</DropdownMenu.Trigger>
      <DropdownMenu.Portal>
        <DropdownMenu.Content align={align} sideOffset={4} collisionPadding={8} className={content}>
          {actions.map((action) => [
            action.separatorBefore && (
              <DropdownMenu.Separator key={`${action.id}-sep`} className={separator} />
            ),
            <DropdownMenu.Item
              key={action.id}
              disabled={action.disabled}
              onSelect={() => onAction(action.id)}
              className={item}
            >
              <Item action={action} />
            </DropdownMenu.Item>,
          ])}
        </DropdownMenu.Content>
      </DropdownMenu.Portal>
    </DropdownMenu.Root>
  );
}

export type ActionContextMenuProps<Id extends string> = {
  children: ReactNode;
  actions: MenuAction<Id>[];
  onAction: (id: Id) => void;
  /** Turn off on touch screens, where long-press selects instead (§16.3). */
  disabled?: boolean;
  onOpenChange?: (open: boolean) => void;
};

/** The right-click menu on a file or folder. */
export function ActionContextMenu<Id extends string>({
  children,
  actions,
  onAction,
  disabled,
  onOpenChange,
}: ActionContextMenuProps<Id>) {
  return (
    <ContextMenu.Root modal={false} onOpenChange={onOpenChange}>
      <ContextMenu.Trigger asChild disabled={disabled}>
        {children}
      </ContextMenu.Trigger>
      <ContextMenu.Portal>
        <ContextMenu.Content collisionPadding={8} className={content}>
          {actions.map((action) => [
            action.separatorBefore && (
              <ContextMenu.Separator key={`${action.id}-sep`} className={separator} />
            ),
            <ContextMenu.Item
              key={action.id}
              disabled={action.disabled}
              onSelect={() => onAction(action.id)}
              className={item}
            >
              <Item action={action} />
            </ContextMenu.Item>,
          ])}
        </ContextMenu.Content>
      </ContextMenu.Portal>
    </ContextMenu.Root>
  );
}
