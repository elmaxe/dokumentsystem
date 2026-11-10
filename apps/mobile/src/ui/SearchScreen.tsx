import type { NodeDto, PathEntry, SearchResult as SearchResultDto } from "@dokument/api";
import { findHits, formatDuration, splitHighlights } from "@dokument/core";
import { useQuery } from "@tanstack/react-query";
import { router } from "expo-router";
import { useRef, useState, type ComponentRef, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { ActivityIndicator, FlatList, Modal, Pressable, ScrollView, TextInput, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { useFormat } from "../state/format";
import { kindOfNode, nodeQuery } from "../state/queries";
import {
  hasFilters,
  modifiedRanges,
  searchQuery,
  searchTypes,
  suggestQuery,
  tagsQuery,
  type SearchParams,
} from "../state/search";
import { useTheme } from "../state/theme";
import { Button, IconButton } from "./Button";
import { EmptyState } from "./EmptyState";
import { Thumbnail } from "./FileList";
import { FileTypeIcon } from "./FileTypeIcon";
import { FolderPicker } from "./FolderPicker";
import { Icon, type IconName } from "./Icon";
import { AccountButton } from "./ListActions";
import { Notice } from "./Notice";
import { Sheet, SheetAction } from "./Sheet";
import { Text } from "./Text";

const pageSize = 50;
/** Operators offered while typing, as on the web's search bar (docs/design.md §13.3). */
const operators = ["type:", "in:", "after:", "tag:", "is:starred"];

/**
 * Search (docs/design.md §13.3, §15.3): names are suggested while typing; a search lists files with their best page,
 * where the hit was and a snippet with the words marked. The chips set the same filters as the query operators.
 * Opening a result opens the viewer on that page with the words marked. Searches aren't stored anywhere.
 */
export function SearchScreen({ initial = {}, back }: { initial?: SearchParams; back?: boolean }) {
  const { t } = useTranslation();
  const theme = useTheme();
  const insets = useSafeAreaInsets();
  const [params, setParams] = useState<SearchParams>(initial);
  const [text, setText] = useState(initial.q ?? "");
  const [typing, setTyping] = useState(false);
  const [limit, setLimit] = useState(pageSize);
  const [sheet, setSheet] = useState<"type" | "modified" | "tag" | "sort" | "folder" | null>(null);
  const input = useRef<ComponentRef<typeof TextInput>>(null);
  const q = params.q ?? "";
  const searching = !!q || hasFilters(params);
  const results = useQuery(searchQuery(params, limit));
  const suggestions = useQuery({ ...suggestQuery(text), enabled: typing && text.trim().length >= 2 });
  const folder = useQuery({ ...nodeQuery(params.folder ?? ""), enabled: !!params.folder });
  const folderName = folder.data?.node.name ?? "…";
  const tags = useQuery(tagsQuery);

  const update = (change: Partial<SearchParams>) => {
    setLimit(pageSize);
    setParams((old) => ({ ...old, ...change }));
  };
  const submit = (value = text) => {
    input.current?.blur();
    setTyping(false);
    update({ q: value.trim() || undefined });
  };
  const clear = () => {
    setText("");
    update({ q: undefined });
    input.current?.focus();
  };
  const leave = () => {
    if (back) router.back();
    else {
      input.current?.blur();
      setTyping(false);
      setText("");
      setParams({});
    }
  };

  const openNode = (node: NodeDto, hit?: SearchResultDto) => {
    if (node.kind === "folder") {
      router.push({ pathname: "/folder/[id]", params: { id: node.id } });
      return;
    }
    router.push({
      pathname: "/file/[id]",
      params: {
        id: node.id,
        ...(q ? { q } : {}),
        ...(hit?.pageNo ? { page: String(hit.pageNo) } : {}),
        ...(hit?.atMs != null ? { at: String(hit.atMs) } : {}),
      },
    });
  };

  const active = searching || typing || back;
  const data = results.data;

  const field = (
    <View className="mx-4 my-2 h-14 flex-row items-center gap-1 rounded-full bg-surface-container-high pl-1 pr-1">
      {active ? (
        <IconButton icon="arrow_back" label={t("common.back")} onPress={leave} color="on-surface" />
      ) : (
        <View className="w-12 items-center">
          <Icon name="search" />
        </View>
      )}
      <TextInput
        ref={input}
        value={text}
        onChangeText={setText}
        onFocus={() => setTyping(true)}
        onBlur={() => setTyping(false)}
        onSubmitEditing={() => submit()}
        placeholder={t("search.placeholder")}
        placeholderTextColor={theme.color("on-surface-variant")}
        accessibilityLabel={t("search.label")}
        returnKeyType="search"
        autoCapitalize="none"
        autoCorrect={false}
        autoFocus={back && !initial.q}
        style={[{ flex: 1, color: theme.color("on-surface"), fontSize: 16, minHeight: 48 }]}
      />
      {text ? <IconButton icon="close" label={t("search.clear")} onPress={clear} /> : null}
    </View>
  );

  const chips = (
    <ScrollView
      horizontal
      showsHorizontalScrollIndicator={false}
      className="grow-0"
      contentContainerClassName="gap-2 px-4 pb-2"
    >
      <Chip
        label={params.type ? t(`kinds.${params.type}`) : t("search.filters.addType")}
        selected={!!params.type}
        menu
        onPress={() => setSheet("type")}
      />
      <Chip
        label={params.modified ? t(`search.modified.${params.modified}`) : t("search.filters.addModified")}
        selected={!!params.modified}
        menu
        onPress={() => setSheet("modified")}
      />
      {params.folder ? (
        <Chip
          label={t("search.chips.inFolder", { value: folderName })}
          selected
          onPress={() => setSheet("folder")}
          onRemove={() => update({ folder: undefined })}
          removeLabel={t("search.removeFilter")}
        />
      ) : (
        <Chip label={t("mobile.search.in")} menu onPress={() => setSheet("folder")} />
      )}
      <Chip
        label={t("search.chips.starred")}
        selected={!!params.starred}
        onPress={() => update({ starred: params.starred ? undefined : true })}
      />
      {params.tag ? (
        <Chip
          label={params.tag}
          icon="sell"
          selected
          onPress={() => setSheet("tag")}
          onRemove={() => update({ tag: undefined })}
          removeLabel={t("search.removeFilter")}
        />
      ) : (tags.data?.length ?? 0) > 0 ? (
        <Chip label={t("search.tagChip")} menu onPress={() => setSheet("tag")} />
      ) : null}
    </ScrollView>
  );

  const heading = !data
    ? null
    : !q
      ? t("search.resultsOnly", { count: data.total })
      : params.folder
        ? t("search.resultsIn", { count: data.total, query: q, folder: folderName })
        : t("search.results", { count: data.total, query: q });

  const body = (): ReactNode => {
    if (typing && text.trim()) {
      return (
        <Suggestions
          text={text}
          items={suggestions.data?.items ?? []}
          onSearch={() => submit()}
          onOpen={(node) => {
            input.current?.blur();
            openNode(node);
          }}
          onOperator={(operator) => setText(`${text.trimEnd()} ${operator}`.trimStart())}
        />
      );
    }
    if (!searching) {
      return (
        <EmptyState icon="manage_search" title={t("search.startTitle")} text={t("search.startText")}>
          <Text variant="mono-sm" className="text-center text-on-surface-variant">
            {t("mobile.search.tip")}
          </Text>
        </EmptyState>
      );
    }
    if (results.isError && !data) return <EmptyState icon="error" title={t("search.failed")} />;
    if (!data) {
      return (
        <View className="flex-1 items-center justify-center">
          <ActivityIndicator color={theme.color("primary")} accessibilityLabel={t("viewer.loading")} />
        </View>
      );
    }
    if (data.results.length === 0) {
      return data.withoutFilters ? (
        <EmptyState
          icon="filter_alt_off"
          title={t("search.noFilteredTitle", { query: q })}
          text={t("search.withoutFilters", { count: data.withoutFilters })}
        >
          <Button
            variant="tonal"
            label={t("search.showAll", { count: data.withoutFilters })}
            onPress={() => update({ type: undefined, modified: undefined, folder: undefined, starred: undefined, tag: undefined })}
          />
        </EmptyState>
      ) : (
        <EmptyState icon="search_off" title={t("search.noResultsTitle", { query: q })} text={t("search.noResultsText")} />
      );
    }
    return (
      <FlatList
        data={data.results}
        keyExtractor={(hit) => hit.node.id}
        keyboardShouldPersistTaps="handled"
        contentContainerStyle={{ paddingBottom: 24 }}
        ListHeaderComponent={
          <View className="gap-2 pb-1">
            <View className="min-h-10 flex-row items-center pl-4 pr-1">
              <Text variant="body-md" accessibilityLiveRegion="polite" className="flex-1 text-on-surface-variant">
                {heading}
              </Text>
              {q ? (
                <Pressable
                  accessibilityRole="button"
                  accessibilityLabel={t("mobile.search.sortBy", { order: t(`search.sort.${params.sort ?? "relevance"}`) })}
                  onPress={() => setSheet("sort")}
                  className="min-h-touch flex-row items-center gap-1 px-3 active:opacity-70"
                >
                  <Icon name="sort" size={18} />
                  <Text variant="label-lg" className="text-on-surface-variant">
                    {t(`search.sort.${params.sort ?? "relevance"}`)}
                  </Text>
                </Pressable>
              ) : null}
            </View>
            {data.pending > 0 ? (
              <View className="px-4">
                <Notice icon="hourglass_top" text={t("search.pending", { count: data.pending })} />
              </View>
            ) : null}
          </View>
        }
        renderItem={({ item }) => <ResultRow hit={item} terms={data.terms} onOpen={() => openNode(item.node, item)} />}
        onEndReachedThreshold={0.5}
        onEndReached={() => {
          if (data.results.length < data.total && !results.isFetching) setLimit(limit + pageSize);
        }}
      />
    );
  };

  const choose = (change: Partial<SearchParams>) => {
    setSheet(null);
    update(change);
  };

  return (
    <View className="flex-1 bg-surface" style={{ paddingTop: insets.top }}>
      {back ? null : (
        <View className="h-topbar flex-row items-center pl-4 pr-2">
          <Text variant="headline-sm" accessibilityRole="header" className="flex-1 text-on-surface">
            {t("nav.search")}
          </Text>
          <AccountButton />
        </View>
      )}
      {field}
      {typing && text.trim() ? null : chips}
      {body()}

      <Sheet open={sheet === "type"} onClose={() => setSheet(null)} title={t("search.filters.addType")}>
        <ScrollView style={{ maxHeight: 480 }}>
          <Choice label={t("mobile.search.anyType")} selected={!params.type} onPress={() => choose({ type: undefined })} />
          {searchTypes.map((type) => (
            <Choice
              key={type}
              label={t(`kinds.${type}`)}
              kind={type}
              selected={params.type === type}
              onPress={() => choose({ type })}
            />
          ))}
        </ScrollView>
      </Sheet>
      <Sheet open={sheet === "modified"} onClose={() => setSheet(null)} title={t("search.filters.addModified")}>
        <Choice label={t("mobile.search.anyTime")} selected={!params.modified} onPress={() => choose({ modified: undefined })} />
        {modifiedRanges.map((range) => (
          <Choice
            key={range}
            label={t(`search.modified.${range}`)}
            selected={params.modified === range}
            onPress={() => choose({ modified: range })}
          />
        ))}
      </Sheet>
      <Sheet open={sheet === "tag"} onClose={() => setSheet(null)} title={t("search.tagChip")}>
        <ScrollView style={{ maxHeight: 480 }}>
          <Choice label={t("search.anyTag")} selected={!params.tag} onPress={() => choose({ tag: undefined })} />
          {(tags.data ?? []).map((tag) => (
            <Choice
              key={tag.name}
              label={`${tag.name} · ${t("tags.files", { count: tag.files })}`}
              selected={params.tag === tag.name}
              onPress={() => choose({ tag: tag.name })}
            />
          ))}
        </ScrollView>
      </Sheet>
      <Sheet open={sheet === "sort"} onClose={() => setSheet(null)} title={t("mobile.search.sort")}>
        {(["relevance", "modified"] as const).map((sort) => (
          <Choice
            key={sort}
            label={t(`search.sort.${sort}`)}
            selected={(params.sort ?? "relevance") === sort}
            onPress={() => choose({ sort: sort === "relevance" ? undefined : sort })}
          />
        ))}
      </Sheet>
      <Modal visible={sheet === "folder"} animationType="slide" onRequestClose={() => setSheet(null)}>
        <View style={[{ flex: 1 }, theme.vars]}>
          <FolderPicker
            title={t("mobile.search.inTitle")}
            start={params.folder ?? null}
            submitLabel={t("mobile.search.searchHere")}
            onCancel={() => setSheet(null)}
            onSubmit={(picked) => choose({ folder: picked.id ?? undefined })}
          />
        </View>
      </Modal>
    </View>
  );
}

/** A filter chip: a menu, a toggle, or a filter that's on, with a way to take it off. */
function Chip({
  label,
  icon,
  selected,
  menu,
  onPress,
  onRemove,
  removeLabel,
}: {
  label: string;
  icon?: IconName;
  selected?: boolean;
  menu?: boolean;
  onPress: () => void;
  onRemove?: () => void;
  removeLabel?: string;
}) {
  const color = selected ? "on-secondary-container" : "on-surface-variant";
  return (
    <View
      className={`h-8 flex-row items-center rounded-sm ${selected ? "bg-secondary-container" : "border border-outline-variant"}`}
    >
      <Pressable
        accessibilityRole={menu ? "button" : "togglebutton"}
        accessibilityState={menu ? undefined : { checked: !!selected }}
        onPress={onPress}
        className={`h-8 flex-row items-center gap-1.5 ${onRemove ? "pl-2 pr-1" : menu ? "pl-3 pr-2" : "px-3"} active:opacity-70`}
      >
        {selected ? <Icon name={icon ?? "check"} size={18} color={color} /> : icon ? <Icon name={icon} size={18} color={color} /> : null}
        <Text variant="label-lg" numberOfLines={1} className={selected ? "text-on-secondary-container" : "text-on-surface-variant"}>
          {label}
        </Text>
        {menu && !onRemove ? <Icon name="arrow_drop_down" size={18} color={color} /> : null}
      </Pressable>
      {onRemove ? (
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={removeLabel}
          onPress={onRemove}
          hitSlop={8}
          className="h-8 justify-center pl-0.5 pr-2 active:opacity-70"
        >
          <Icon name="close" size={18} color={color} />
        </Pressable>
      ) : null}
    </View>
  );
}

/** One choice in a filter's sheet. */
function Choice({
  label,
  kind,
  selected,
  onPress,
}: {
  label: string;
  kind?: (typeof searchTypes)[number];
  selected: boolean;
  onPress: () => void;
}) {
  return (
    <SheetAction
      selected={selected}
      onPress={onPress}
      icon={
        <View className="flex-row items-center gap-4">
          <Icon
            name={selected ? "radio_button_checked" : "radio_button_unchecked"}
            color={selected ? "primary" : "on-surface-variant"}
          />
          {kind ? <FileTypeIcon kind={kind} /> : null}
        </View>
      }
      label={label}
    />
  );
}

/** While typing: search for the words, names that match, and the operators to narrow it down. */
function Suggestions({
  text,
  items,
  onSearch,
  onOpen,
  onOperator,
}: {
  text: string;
  items: { node: NodeDto; path: PathEntry[] }[];
  onSearch: () => void;
  onOpen: (node: NodeDto) => void;
  onOperator: (operator: string) => void;
}) {
  const { t } = useTranslation();
  const terms = text.trim().split(/\s+/);
  return (
    <ScrollView keyboardShouldPersistTaps="handled">
      <Pressable
        accessibilityRole="button"
        onPress={onSearch}
        className="min-h-14 flex-row items-center gap-4 px-4 active:bg-surface-container-highest"
      >
        <Icon name="search" />
        <Text numberOfLines={2} className="flex-1 text-on-surface">
          {t("search.searchFor", { query: text.trim() })}
        </Text>
      </Pressable>
      {items.length > 0 ? (
        <Text variant="title-sm" className="px-4 pb-1 pt-3 text-on-surface-variant">
          {t("search.names")}
        </Text>
      ) : null}
      {items.map(({ node, path }) => (
        <Pressable
          key={node.id}
          accessibilityRole="button"
          onPress={() => onOpen(node)}
          className="min-h-14 flex-row items-center gap-4 px-4 py-1 active:bg-surface-container-highest"
        >
          <Thumbnail node={node} size="small" className="h-10 w-10 rounded-sm" />
          <View className="min-w-0 flex-1">
            <Text numberOfLines={1} className="text-on-surface">
              <Marked text={node.name} terms={terms} bold />
            </Text>
            {path.length > 0 ? (
              <Text variant="body-sm" numberOfLines={1} className="text-on-surface-variant">
                {path.map((entry) => entry.name).join(" / ")}
              </Text>
            ) : null}
          </View>
        </Pressable>
      ))}
      <Text variant="title-sm" className="px-4 pb-2 pt-4 text-on-surface-variant">
        {t("search.narrow")}
      </Text>
      <View className="flex-row flex-wrap gap-2 px-4 pb-6">
        {operators.map((operator) => (
          <Pressable
            key={operator}
            accessibilityRole="button"
            onPress={() => onOperator(operator)}
            className="h-8 justify-center rounded-sm border border-outline-variant px-3 active:opacity-70"
          >
            <Text variant="mono-sm" className="text-on-surface-variant">
              {operator}
            </Text>
          </Pressable>
        ))}
      </View>
    </ScrollView>
  );
}

/**
 * Text with the hits marked: the server's ranges for a snippet, or the search terms by the shared rule (§13.3).
 * Text is never parsed as markup, so a file's content can't inject any.
 */
export function Marked({
  text,
  ranges,
  terms,
  bold,
}: {
  text: string;
  ranges?: readonly (readonly number[])[];
  terms?: readonly string[];
  /** As suggestions mark what was typed, instead of the highlight colour. */
  bold?: boolean;
}) {
  const parts = splitHighlights(text, ranges ?? findHits(text, terms ?? []));
  return (
    <>
      {parts.map((part, i) =>
        part.hit ? (
          <Text key={i} className={bold ? "font-semibold" : "rounded-xs bg-warning-container text-on-surface"}>
            {part.text}
          </Text>
        ) : (
          part.text
        ),
      )}
    </>
  );
}

/** One result: the file, its best snippet, and where it is, which page and where the words were found. */
function ResultRow({ hit, terms, onOpen }: { hit: SearchResultDto; terms: string[]; onOpen: () => void }) {
  const { t } = useTranslation();
  const format = useFormat();
  const node = hit.node;
  const kind = kindOfNode(node);
  const root = node.access === "owner" ? t("nav.myFiles") : t("nav.sharedWithMe");
  const location = [root, ...hit.path.map((entry) => entry.name)].join(" / ");
  const page =
    hit.atMs != null
      ? t("search.at", { time: formatDuration(Math.max(0, hit.atMs) / 1000) })
      : hit.pageNo && hit.pageCount
        ? t(kind === "slides" ? "search.slide" : "search.page", { page: hit.pageNo, total: hit.pageCount })
        : hit.pageNo && kind === "pdf"
          ? t("search.pageOnly", { page: hit.pageNo })
          : null;
  const found =
    hit.source === "ocr"
      ? kind === "image"
        ? t("search.foundInPhoto")
        : t("search.foundInScan")
      : hit.source === "metadata"
        ? t("search.foundInDetails")
        : hit.source === "tag"
          ? t("search.foundInTags")
          : hit.source === "transcript"
            ? t("search.foundInSpeech")
            : hit.source === "text"
              ? t("search.foundInText")
              : null;
  const snippet = hit.snippet;
  const details = [
    page,
    hit.pagesWithHits > 1 && hit.atMs == null ? t("search.pagesWithHits", { count: hit.pagesWithHits }) : null,
  ].filter(Boolean);

  return (
    <Pressable
      accessibilityRole="button"
      onPress={onOpen}
      className="flex-row items-start gap-4 py-2.5 pl-4 pr-4 active:bg-surface-container-highest"
    >
      <Thumbnail node={node} size="small" className="mt-0.5 h-12 w-12 rounded-sm" />
      <View className="min-w-0 flex-1 gap-0.5">
        <Text variant="title-sm" numberOfLines={1} className="text-on-surface">
          <Marked text={node.name} terms={terms} />
        </Text>
        {snippet?.text ? (
          <Text variant="body-md" numberOfLines={2} className="text-on-surface-variant">
            {details.length ? `${details.join(" · ")} · ` : ""}
            {snippet.cutStart ? "…" : ""}
            <Marked text={snippet.text} ranges={snippet.highlights} />
            {snippet.cutEnd ? "…" : ""}
          </Text>
        ) : details.length ? (
          <Text variant="body-md" className="text-on-surface-variant">
            {details.join(" · ")}
          </Text>
        ) : null}
        <Text variant="body-sm" numberOfLines={1} className="text-on-surface-variant">
          {[location, found ?? format.date(node.modifiedAt)].join(" · ")}
        </Text>
      </View>
    </Pressable>
  );
}
