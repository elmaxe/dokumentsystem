import { useId, type InputHTMLAttributes, type ReactNode, type Ref } from "react";
import { cn } from "../lib/cn";
import { Icon } from "./Icon";

export type TextFieldProps = InputHTMLAttributes<HTMLInputElement> & {
  ref?: Ref<HTMLInputElement>;
  label?: string;
  icon?: string;
  /** A sentence under the field. Errors say what happened and what to do (§16.5). */
  hint?: ReactNode;
  error?: string;
  mono?: boolean;
};

export function TextField({ label, icon, hint, error, mono, className, id, ...props }: TextFieldProps) {
  const fallbackId = useId();
  const inputId = id ?? fallbackId;
  const hintId = `${inputId}-hint`;
  return (
    <div className={cn("flex min-w-0 flex-col gap-1", className)}>
      {label && (
        <label htmlFor={inputId} className="text-label-lg text-on-surface">
          {label}
        </label>
      )}
      <div
        className={cn(
          "flex h-control min-w-0 items-center gap-2 rounded-sm border bg-surface-container-lowest px-3 text-on-surface focus-within:outline-2 focus-within:outline-offset-2 focus-within:outline-focus-ring pointer-coarse:h-touch",
          error ? "border-error" : "border-outline",
        )}
      >
        {icon && <Icon name={icon} className="text-on-surface-variant" />}
        <input
          id={inputId}
          aria-describedby={hint || error ? hintId : undefined}
          aria-invalid={error ? true : undefined}
          {...props}
          className={cn(
            "min-w-0 flex-1 bg-transparent outline-none placeholder:text-on-surface-variant",
            mono ? "font-mono text-mono-sm" : "text-body-md pointer-coarse:text-body-lg",
          )}
        />
      </div>
      {(error || hint) && (
        <p
          id={hintId}
          className={cn(
            "m-0 flex items-start gap-1 text-body-sm",
            error ? "text-error" : "text-on-surface-variant",
          )}
        >
          {error && <Icon name="error" size={16} filled />}
          {error ?? hint}
        </p>
      )}
    </div>
  );
}
