import { Dialog as DialogPrimitive } from "radix-ui";
import { useRef, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { cn } from "../lib/cn";
import { IconButton } from "./Button";

export type DialogVariant = "auto" | "dialog" | "sheet";

export type DialogProps = {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: ReactNode;
  /** A sentence under the title, for screen readers and sighted people alike. */
  description?: ReactNode;
  headAction?: ReactNode;
  children?: ReactNode;
  /** The buttons, end-aligned. The confirming action goes last. */
  footer?: ReactNode;
  /** auto is a bottom sheet under 600px and a dialog above (§16.3). */
  variant?: DialogVariant;
  width?: "sm" | "md" | "lg";
};

const widths = { sm: "sm:w-100", md: "sm:w-130", lg: "sm:w-170" };

export function Dialog({
  open,
  onOpenChange,
  title,
  description,
  headAction,
  children,
  footer,
  variant = "auto",
  width = "md",
}: DialogProps) {
  const { t } = useTranslation();
  const contentRef = useRef<HTMLDivElement>(null);
  const sheet = variant === "sheet";
  const dialog = variant === "dialog";
  return (
    <DialogPrimitive.Root open={open} onOpenChange={onOpenChange}>
      <DialogPrimitive.Portal>
        <DialogPrimitive.Overlay className="fixed inset-0 z-40 bg-scrim" />
        <DialogPrimitive.Content
          ref={contentRef}
          // Start in the first text field if there is one, otherwise on the dialog itself, so the
          // first button doesn't open with a focus ring.
          onOpenAutoFocus={(event) => {
            event.preventDefault();
            (contentRef.current?.querySelector("input") ?? contentRef.current)?.focus();
          }}
          className={cn(
            "fixed z-50 flex max-h-[calc(100dvh-32px)] flex-col bg-surface-container-high text-on-surface shadow-elevation-3 outline-none",
            sheet && "inset-x-0 bottom-0 rounded-t-lg",
            dialog && "top-1/2 left-1/2 w-[calc(100%-48px)] -translate-1/2 rounded-lg",
            variant === "auto" &&
              "inset-x-0 bottom-0 rounded-t-lg sm:inset-x-auto sm:top-1/2 sm:bottom-auto sm:left-1/2 sm:max-w-[calc(100%-48px)] sm:-translate-1/2 sm:rounded-lg",
            !sheet && widths[width],
            dialog && "max-w-[calc(100%-48px)]",
          )}
        >
          {!dialog && (
            <div
              aria-hidden
              className={cn(
                "mx-auto mt-3 h-1 w-8 shrink-0 rounded-full bg-outline/60",
                variant === "auto" && "sm:hidden",
              )}
            />
          )}
          <div
            className={cn(
              "flex shrink-0 items-start gap-3",
              sheet ? "px-4 py-3" : dialog ? "px-6 pt-6 pb-4" : "px-4 py-3 sm:px-6 sm:pt-6 sm:pb-4",
            )}
          >
            <DialogPrimitive.Title className="m-0 min-w-0 flex-1 text-title-lg">
              {title}
            </DialogPrimitive.Title>
            {headAction}
            {variant !== "dialog" && (
              <DialogPrimitive.Close asChild>
                <IconButton
                  icon="close"
                  label={t("common.close")}
                  size="sm"
                  className={cn(variant === "auto" && "sm:hidden")}
                />
              </DialogPrimitive.Close>
            )}
          </div>
          {description ? (
            <DialogPrimitive.Description
              className={cn(
                "m-0 -mt-2 pb-4 text-body-md text-on-surface-variant",
                sheet ? "px-4" : dialog ? "px-6" : "px-4 sm:px-6",
              )}
            >
              {description}
            </DialogPrimitive.Description>
          ) : (
            <DialogPrimitive.Description className="sr-only">{title}</DialogPrimitive.Description>
          )}
          <div
            className={cn(
              "flex min-h-0 flex-col gap-5 overflow-y-auto text-on-surface-variant",
              sheet
                ? "px-4 text-body-lg"
                : dialog
                  ? "px-6 text-body-md"
                  : "px-4 text-body-lg sm:px-6 sm:text-body-md",
            )}
          >
            {children}
          </div>
          {footer && (
            <div
              className={cn(
                "flex shrink-0 flex-wrap justify-end gap-2",
                sheet
                  ? "p-4 *:h-touch *:flex-1"
                  : dialog
                    ? "p-6"
                    : "p-4 max-sm:*:h-touch max-sm:*:flex-1 sm:p-6",
              )}
            >
              {footer}
            </div>
          )}
        </DialogPrimitive.Content>
      </DialogPrimitive.Portal>
    </DialogPrimitive.Root>
  );
}
