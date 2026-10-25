import { useId, useState, type KeyboardEvent } from "react";
import { useTranslation } from "react-i18next";
import { cn } from "../lib/cn";
import { Button } from "./Button";
import { Dialog } from "./Dialog";
import { Icon } from "./Icon";

/** Where a tag came from (docs/design.md §13.4): a person, a keyword rule, or the ML service. */
export type TagSource = "user" | "rule" | "ml";

export type TagInfo = {
  name: string;
  source: TagSource;
  /** For a rule's tag, the rule's name, shown on hover. */
  ruleName?: string;
};

export type TagSuggestionItem = { name: string; files: number };

const sourceIcons: Record<TagSource, string | null> = {
  user: null,
  rule: "bolt",
  ml: "auto_awesome",
};

/** Splits what someone typed into tags: commas separate them, and blanks are dropped. */
export function splitTags(value: string): string[] {
  return value
    .split(",")
    .map((part) => part.trim().replace(/\s+/g, " "))
    .filter(Boolean);
}

export type TagChipProps = {
  tag: TagInfo;
  /** Small chips for list rows and search results; they can't be removed. */
  size?: "sm" | "md";
  onOpen?: () => void;
  onRemove?: () => void;
  className?: string;
};

/**
 * A tag on a file (§16.4). Tags people add are outlined; automatic ones are tinted with a small icon, so they read as
 * automatic, and say on hover which rule added them.
 */
export function TagChip({ tag, size = "md", onOpen, onRemove, className }: TagChipProps) {
  const { t } = useTranslation();
  const icon = sourceIcons[tag.source];
  const automatic = tag.source !== "user";
  const title =
    tag.source === "rule"
      ? t("tags.byRule", { name: tag.ruleName ?? tag.name })
      : tag.source === "ml"
        ? t("tags.byMl")
        : undefined;
  const small = size === "sm";
  const body = (
    <>
      {icon && <Icon name={icon} size={small ? 14 : 16} />}
      <span className="truncate">{tag.name}</span>
    </>
  );
  return (
    <span
      title={title}
      className={cn(
        "inline-flex max-w-full min-w-0 shrink-0 items-center border whitespace-nowrap",
        small ? "h-6 gap-0.5 rounded-xs px-2 text-label-sm" : "h-8 gap-1 rounded-sm text-label-lg",
        !small && (onRemove ? "pr-1" : "pr-3"),
        !small && (icon ? "pl-2" : "pl-3"),
        automatic
          ? "border-transparent bg-secondary-container text-on-secondary-container"
          : "border-outline-variant bg-surface-container-lowest text-on-surface",
        className,
      )}
    >
      {onOpen ? (
        <button
          type="button"
          onClick={onOpen}
          aria-label={t("tags.search", { name: tag.name })}
          className="inline-flex min-w-0 cursor-pointer items-center gap-1 rounded-xs hover:underline"
        >
          {body}
        </button>
      ) : (
        body
      )}
      {onRemove && (
        <button
          type="button"
          onClick={onRemove}
          aria-label={t("tags.remove", { name: tag.name })}
          title={t("tags.remove", { name: tag.name })}
          className="ml-0.5 inline-flex cursor-pointer rounded-full p-0.5 text-current/80 hover:bg-on-surface/8 pointer-coarse:p-1.5"
        >
          <Icon name="close" size={18} />
        </button>
      )}
    </span>
  );
}

/** Up to <c>max</c> small chips after a name in a list row, then "+N". */
export function TagStrip({
  tags,
  max = 2,
  className,
}: {
  tags: TagInfo[];
  max?: number;
  className?: string;
}) {
  const { t } = useTranslation();
  if (tags.length === 0) return null;
  return (
    <span className={cn("inline-flex min-w-0 items-center gap-1", className)}>
      {tags.slice(0, max).map((tag) => (
        <TagChip key={tag.name} tag={tag} size="sm" />
      ))}
      {tags.length > max && (
        <span className="text-label-sm text-on-surface-variant">
          {t("tags.more", { count: tags.length - max })}
        </span>
      )}
    </span>
  );
}

export type TagInputProps = {
  /** Tags already there, left out of the suggestions. */
  exclude: readonly string[];
  /** Tags to suggest for what's typed so far. */
  suggestions: TagSuggestionItem[];
  onQueryChange: (query: string) => void;
  onAdd: (names: string[]) => void;
  onClose?: () => void;
  label: string;
  autoFocus?: boolean;
};

/**
 * Where a tag is typed (§13.4): suggestions from tags already on files the person can see, most used first, then
 * "Create tag". Enter adds, commas separate several, Esc closes.
 */
export function TagInput({
  exclude,
  suggestions,
  onQueryChange,
  onAdd,
  onClose,
  label,
  autoFocus,
}: TagInputProps) {
  const { t } = useTranslation();
  const id = useId();
  const [value, setValue] = useState("");
  const [active, setActive] = useState(0);
  const typed = value.trim().replace(/\s+/g, " ");
  const taken = new Set(exclude.map((name) => name.toLowerCase()));
  const options = suggestions.filter((s) => !taken.has(s.name.toLowerCase())).slice(0, 6);
  const exact = options.some((o) => o.name.toLowerCase() === typed.toLowerCase());
  const create =
    typed && !typed.includes(",") && !exact && !taken.has(typed.toLowerCase()) ? typed : null;
  const rows = [...options.map((o) => o.name), ...(create ? [create] : [])];
  const tooLong = splitTags(value).some((name) => name.length > 50);

  const change = (next: string) => {
    setValue(next);
    setActive(0);
    onQueryChange(next.split(",").at(-1)?.trim() ?? "");
  };
  const add = (names: string[]) => {
    const wanted = names.filter((name) => name.length <= 50 && !taken.has(name.toLowerCase()));
    if (wanted.length > 0) onAdd(wanted);
    change("");
  };
  const onKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === "Escape") {
      event.preventDefault();
      onClose?.();
    } else if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      if (rows.length > 0)
        setActive((active + (event.key === "ArrowDown" ? 1 : rows.length - 1)) % rows.length);
    } else if (event.key === "Enter") {
      event.preventDefault();
      const parts = splitTags(value);
      if (parts.length > 1) add(parts);
      else if (rows[active]) add([rows[active]]);
    }
  };

  return (
    <div className="flex flex-col gap-1">
      <div className="flex h-control items-center gap-2 rounded-sm border border-outline bg-surface-container-lowest px-3 focus-within:outline-2 focus-within:outline-offset-2 focus-within:outline-focus-ring pointer-coarse:h-touch">
        <Icon name="sell" className="text-on-surface-variant" />
        <input
          role="combobox"
          aria-label={label}
          aria-expanded={rows.length > 0}
          aria-controls={`${id}-list`}
          aria-activedescendant={rows.length > 0 ? `${id}-${active}` : undefined}
          autoFocus={autoFocus}
          value={value}
          onChange={(event) => change(event.target.value)}
          onKeyDown={onKeyDown}
          placeholder={label}
          maxLength={500}
          className="min-w-0 flex-1 bg-transparent text-body-md outline-none placeholder:text-on-surface-variant pointer-coarse:text-body-lg"
        />
      </div>
      {rows.length > 0 && (
        <div
          role="listbox"
          id={`${id}-list`}
          aria-label={label}
          className="rounded-xs bg-surface-container py-2 shadow-elevation-2"
        >
          {options.map((option, index) => (
            <div
              key={option.name}
              id={`${id}-${index}`}
              role="option"
              aria-selected={active === index}
              onPointerDown={(event) => event.preventDefault()}
              onPointerEnter={() => setActive(index)}
              onClick={() => add([option.name])}
              className={cn(
                "flex min-h-11 cursor-pointer items-center gap-3 px-3 text-body-md pointer-coarse:min-h-touch",
                active === index && "bg-on-surface/8",
              )}
            >
              <Icon name="sell" className="text-on-surface-variant" />
              <span className="min-w-0 flex-1 truncate">{option.name}</span>
              <span className="tabular text-body-sm text-on-surface-variant">
                {t("tags.files", { count: option.files })}
              </span>
            </div>
          ))}
          {create && (
            <div
              id={`${id}-${options.length}`}
              role="option"
              aria-selected={active === options.length}
              onPointerDown={(event) => event.preventDefault()}
              onPointerEnter={() => setActive(options.length)}
              onClick={() => add([create])}
              className={cn(
                "flex min-h-11 cursor-pointer items-center gap-3 px-3 text-body-md pointer-coarse:min-h-touch",
                active === options.length && "bg-on-surface/8",
              )}
            >
              <Icon name="add" className="text-on-surface-variant" />
              <span className="min-w-0 flex-1 truncate">{t("tags.create", { name: create })}</span>
            </div>
          )}
        </div>
      )}
      <p className={cn("m-0 text-body-sm", tooLong ? "text-error" : "text-on-surface-variant")}>
        {tooLong ? t("tags.tooLong") : t("tags.addHint")}
      </p>
    </div>
  );
}

export type TagEditorProps = {
  tags: TagInfo[];
  /** Automatic tags someone took off this file, which can be put back. */
  removed?: string[];
  /** Whether the person may add and remove tags. */
  canEdit: boolean;
  suggestions: TagSuggestionItem[];
  onQueryChange: (query: string) => void;
  onAdd: (names: string[]) => void;
  onRemove: (tag: TagInfo) => void;
  onPutBack?: (name: string) => void;
  /** Searches for files with the tag. */
  onOpen?: (tag: TagInfo) => void;
};

/** The Tags section of the details panel's Info tab (§13.4, §16.4). */
export function TagEditor({
  tags,
  removed = [],
  canEdit,
  suggestions,
  onQueryChange,
  onAdd,
  onRemove,
  onPutBack,
  onOpen,
}: TagEditorProps) {
  const { t } = useTranslation();
  const [adding, setAdding] = useState(false);
  return (
    <section aria-label={t("tags.title")} className="mt-3 border-t border-outline-variant pt-3">
      <h3 className="m-0 flex min-h-10 items-center text-title-sm">{t("tags.title")}</h3>
      <div className="flex flex-wrap items-center gap-2">
        {tags.map((tag) => (
          <TagChip
            key={tag.name}
            tag={tag}
            onOpen={onOpen ? () => onOpen(tag) : undefined}
            onRemove={canEdit ? () => onRemove(tag) : undefined}
          />
        ))}
        {canEdit && !adding && (
          <button
            type="button"
            onClick={() => setAdding(true)}
            className="inline-flex h-8 cursor-pointer items-center gap-1 rounded-sm border border-dashed border-outline pr-3 pl-2 text-label-lg text-primary hover:bg-primary/8 pointer-coarse:h-10"
          >
            <Icon name="add" size={18} />
            {t("tags.add")}
          </button>
        )}
        {!canEdit && tags.length === 0 && (
          <p className="m-0 text-body-md text-on-surface-variant">{t("tags.readOnly")}</p>
        )}
      </div>
      {adding && (
        <div className="mt-3">
          <TagInput
            autoFocus
            label={t("tags.addLabel")}
            exclude={tags.map((tag) => tag.name)}
            suggestions={suggestions}
            onQueryChange={onQueryChange}
            onAdd={onAdd}
            onClose={() => setAdding(false)}
          />
        </div>
      )}
      {canEdit && removed.length > 0 && (
        <p className="m-0 mt-2 text-body-sm text-on-surface-variant">
          {t("tags.removedLabel")}{" "}
          {removed.map((name, index) => (
            <span key={name}>
              {index > 0 && ", "}
              <i>{name}</i>{" "}
              {onPutBack && (
                <button
                  type="button"
                  onClick={() => onPutBack(name)}
                  className="cursor-pointer font-semibold text-primary hover:underline"
                >
                  {t("tags.putBack")}
                </button>
              )}
            </span>
          ))}
          . {t("tags.removedNote")}
        </p>
      )}
    </section>
  );
}

export type TagsDialogProps = {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** How many files are selected. */
  count: number;
  /** Each tag on any of the files, with how many of them have it. */
  tags: (TagInfo & { files: number })[];
  suggestions: TagSuggestionItem[];
  onQueryChange: (query: string) => void;
  onSave: (change: { add: string[]; remove: string[] }) => void;
  saving?: boolean;
};

/**
 * Tags for several files at once (§13.4): the tags all of them have, those only some have (dashed, "1 of 3"), and a
 * field that adds a tag to all. Nothing changes until Save.
 */
export function TagsDialog({
  open,
  onOpenChange,
  count,
  tags,
  suggestions,
  onQueryChange,
  onSave,
  saving,
}: TagsDialogProps) {
  const { t } = useTranslation();
  const [add, setAdd] = useState<string[]>([]);
  const [remove, setRemove] = useState<string[]>([]);
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setAdd([]);
      setRemove([]);
    }
  }

  const key = (name: string) => name.toLowerCase();
  const removing = new Set(remove.map(key));
  const adding = new Set(add.map(key));
  const shown = tags
    .filter((tag) => !removing.has(key(tag.name)))
    .map((tag) => (adding.has(key(tag.name)) ? { ...tag, files: count } : tag));
  const extra = add.filter((name) => !tags.some((tag) => key(tag.name) === key(name)));
  const onAll = [
    ...shown.filter((tag) => tag.files >= count),
    ...extra.map((name): TagInfo & { files: number } => ({ name, source: "user", files: count })),
  ];
  const onSome = shown.filter((tag) => tag.files < count);
  const changes = add.length + remove.length;

  const addTag = (name: string) => {
    setRemove((list) => list.filter((n) => key(n) !== key(name)));
    setAdd((list) => (list.some((n) => key(n) === key(name)) ? list : [...list, name]));
  };
  const removeTag = (name: string) => {
    setAdd((list) => list.filter((n) => key(n) !== key(name)));
    if (tags.some((tag) => key(tag.name) === key(name))) setRemove((list) => [...list, name]);
  };

  return (
    <Dialog
      open={open}
      onOpenChange={onOpenChange}
      title={t("tags.dialogTitle", { count })}
      description={count > 1 ? t("tags.dialogHint") : undefined}
      footer={
        <>
          <Button variant="text" onClick={() => onOpenChange(false)}>
            {t("common.cancel")}
          </Button>
          <Button disabled={changes === 0 || saving} onClick={() => onSave({ add, remove })}>
            {t("common.save")}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        {onAll.length > 0 && (
          <div className="flex flex-col gap-2">
            <span className="text-body-sm text-on-surface-variant">
              {t("tags.onAll", { count })}
            </span>
            <div className="flex flex-wrap gap-2">
              {onAll.map((tag) => (
                <TagChip key={tag.name} tag={tag} onRemove={() => removeTag(tag.name)} />
              ))}
            </div>
          </div>
        )}
        {onSome.length > 0 && (
          <div className="flex flex-col gap-2">
            <span className="text-body-sm text-on-surface-variant">{t("tags.onSome")}</span>
            <div className="flex flex-wrap gap-2">
              {onSome.map((tag) => (
                <span
                  key={tag.name}
                  className="inline-flex h-8 items-center gap-0.5 rounded-sm border border-dashed border-outline pr-1 pl-3 text-label-lg text-on-surface-variant"
                >
                  {tag.source !== "user" && <Icon name={sourceIcons[tag.source]!} size={16} />}
                  <span className="tabular">
                    {t("tags.someCount", { name: tag.name, count: tag.files, total: count })}
                  </span>
                  <button
                    type="button"
                    onClick={() => addTag(tag.name)}
                    aria-label={`${t("tags.addToAll")}: ${tag.name}`}
                    title={t("tags.addToAll")}
                    className="inline-flex cursor-pointer rounded-full p-0.5 hover:bg-on-surface/8"
                  >
                    <Icon name="add" size={18} />
                  </button>
                  <button
                    type="button"
                    onClick={() => removeTag(tag.name)}
                    aria-label={`${t("tags.removeFromAll")}: ${tag.name}`}
                    title={t("tags.removeFromAll")}
                    className="inline-flex cursor-pointer rounded-full p-0.5 hover:bg-on-surface/8"
                  >
                    <Icon name="close" size={18} />
                  </button>
                </span>
              ))}
            </div>
          </div>
        )}
        <TagInput
          label={t("tags.addToAllLabel", { count })}
          exclude={onAll.map((tag) => tag.name)}
          suggestions={suggestions}
          onQueryChange={onQueryChange}
          onAdd={(names) => names.forEach(addTag)}
        />
        {changes > 0 && (
          <p className="m-0 text-body-sm text-on-surface-variant">
            {t("tags.pending", { count: changes })}
          </p>
        )}
      </div>
    </Dialog>
  );
}
