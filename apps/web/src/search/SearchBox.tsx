import { Chip, SearchBar, SearchSuggestions, type FileItem } from "@dokument/ui";
import { useQuery } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useEffect, useId, useRef, useState, type KeyboardEvent } from "react";
import { useTranslation } from "react-i18next";
import { pathLabel, toItem } from "../files/drive";
import { suggestQuery, type SearchParams } from "./search";

/**
 * The search bar in the top bar (docs/design.md §16.4). As someone types it suggests names that match; Enter searches
 * names and text on the search page. On the search page it shows the current query and a back arrow.
 */
export function SearchBox({
  params,
  folderId,
  onBack,
  className,
}: {
  /** The search page's parameters, when it's open. */
  params?: SearchParams;
  /** The folder being browsed, for "In this folder". */
  folderId: string | null;
  onBack?: () => void;
  className?: string;
}) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const listId = useId();
  const input = useRef<HTMLInputElement>(null);
  const urlQuery = params?.q ?? "";
  const [value, setValue] = useState(urlQuery);
  const [shown, setShown] = useState(urlQuery);
  const [focused, setFocused] = useState(false);
  const [active, setActive] = useState(-1);
  const [typed, setTyped] = useState(urlQuery);

  // The URL wins when it changes from elsewhere: a chip, the back button, a link.
  if (urlQuery !== shown) {
    setShown(urlQuery);
    setValue(urlQuery);
  }

  // Suggestions follow typing after a short pause, so each key press isn't a request.
  useEffect(() => {
    const timer = setTimeout(() => setTyped(value), 150);
    return () => clearTimeout(timer);
  }, [value]);

  const suggest = useQuery({ ...suggestQuery(typed.trim()), enabled: focused && typed.trim().length >= 2 });
  const open = focused && value.trim().length >= 2 && value !== urlQuery;
  const items = open ? (suggest.data?.items ?? []) : [];

  const search = (change: Partial<SearchParams> = {}) => {
    const q = value.trim();
    setActive(-1);
    input.current?.blur();
    void navigate({ to: "/search", search: { ...(params ?? {}), q: q || undefined, ...change } });
  };

  const pick = (item: FileItem) => {
    setActive(-1);
    setValue(urlQuery);
    input.current?.blur();
    void (item.kind === "folder"
      ? navigate({ to: "/folders/$folderId", params: { folderId: item.id } })
      : navigate({ to: "/files/$fileId", params: { fileId: item.id } }));
  };

  const onKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === "Escape") {
      if (open) setValue(urlQuery);
      else input.current?.blur();
      setActive(-1);
    } else if (open && (event.key === "ArrowDown" || event.key === "ArrowUp")) {
      event.preventDefault();
      const count = items.length + 1;
      setActive((((active + 1 + (event.key === "ArrowDown" ? 1 : -1)) % count) + count) % count - 1);
    } else if (open && event.key === "Enter" && active >= 0 && items[active]) {
      event.preventDefault();
      pick(toItem(items[active].node));
    }
  };

  return (
    <div className={`relative ${className ?? ""}`}>
      <SearchBar
        value={value}
        onChange={(next) => {
          setValue(next);
          setActive(-1);
        }}
        onSubmit={() => search()}
        onBack={onBack}
        inputRef={input}
        onFocus={() => setFocused(true)}
        onBlur={() => setFocused(false)}
        onKeyDown={onKeyDown}
        suggestions={{ listId, open, activeId: active < 0 ? `${listId}-search` : `${listId}-${active}` }}
        className={open ? "rounded-b-none" : undefined}
      />
      {open && (
        <SearchSuggestions
          id={listId}
          query={value.trim()}
          terms={suggest.data?.terms ?? []}
          suggestions={items.map((s) => ({ item: toItem(s.node), location: pathLabel(t("nav.myFiles"), s.path) }))}
          active={active}
          onActiveChange={setActive}
          onSearch={() => search()}
          onPick={pick}
          filters={
            <>
              <Chip icon="picture_as_pdf" onClick={() => search({ type: "pdf" })}>
                {t("kinds.pdf")}
              </Chip>
              <Chip icon="image" onClick={() => search({ type: "image" })}>
                {t("kinds.image")}
              </Chip>
              {folderId && (
                <Chip icon="folder" onClick={() => search({ folder: folderId })}>
                  {t("search.inThisFolder")}
                </Chip>
              )}
            </>
          }
          className="absolute inset-x-0 top-full rounded-b-xl"
        />
      )}
    </div>
  );
}
