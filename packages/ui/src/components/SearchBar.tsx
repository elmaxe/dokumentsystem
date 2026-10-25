import type { FocusEvent, KeyboardEvent, Ref } from "react";
import { useTranslation } from "react-i18next";
import { cn } from "../lib/cn";
import { IconButton } from "./Button";
import { Icon } from "./Icon";

export type SearchBarProps = {
  value: string;
  onChange: (value: string) => void;
  onSubmit?: (value: string) => void;
  /** Shows a back arrow instead of the search icon, for the phone's full-screen search. */
  onBack?: () => void;
  className?: string;
  autoFocus?: boolean;
  inputRef?: Ref<HTMLInputElement>;
  onFocus?: (event: FocusEvent<HTMLInputElement>) => void;
  onBlur?: (event: FocusEvent<HTMLInputElement>) => void;
  onKeyDown?: (event: KeyboardEvent<HTMLInputElement>) => void;
  /** When suggestions show under the field: the listbox's id and the highlighted option's id. */
  suggestions?: { listId: string; open: boolean; activeId?: string };
};

/** The pill search field in the top bar (§16.2: radius xl, surface-container-high). */
export function SearchBar({
  value,
  onChange,
  onSubmit,
  onBack,
  className,
  autoFocus,
  inputRef,
  onFocus,
  onBlur,
  onKeyDown,
  suggestions,
}: SearchBarProps) {
  const { t } = useTranslation();
  return (
    <form
      role="search"
      onSubmit={(event) => {
        event.preventDefault();
        onSubmit?.(value);
      }}
      className={cn(
        "flex h-12 min-w-0 items-center gap-2 rounded-xl bg-surface-container-high px-2 focus-within:outline-2 focus-within:outline-offset-2 focus-within:outline-focus-ring",
        className,
      )}
    >
      {onBack ? (
        <IconButton icon="arrow_back" label={t("common.back")} onClick={onBack} />
      ) : (
        <Icon name="search" className="ml-2 text-on-surface-variant" />
      )}
      <input
        ref={inputRef}
        type="search"
        value={value}
        autoFocus={autoFocus}
        enterKeyHint="search"
        onChange={(event) => onChange(event.target.value)}
        onFocus={onFocus}
        onBlur={onBlur}
        onKeyDown={onKeyDown}
        {...(suggestions && {
          role: "combobox",
          "aria-autocomplete": "list" as const,
          "aria-controls": suggestions.listId,
          "aria-expanded": suggestions.open,
          "aria-activedescendant": suggestions.open ? suggestions.activeId : undefined,
        })}
        placeholder={t("search.placeholder")}
        aria-label={t("search.label")}
        className="min-w-0 flex-1 bg-transparent text-body-lg text-on-surface outline-none placeholder:text-on-surface-variant [&::-webkit-search-cancel-button]:hidden"
      />
      {value && (
        <IconButton icon="close" label={t("search.clear")} onClick={() => onChange("")} />
      )}
    </form>
  );
}
