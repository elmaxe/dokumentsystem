import type {
  FileDetailsDto,
  FileTextDto,
  NodeDto,
  PathEntry,
  ProcessingStep,
  Tag,
  VersionDto,
} from "@dokument/api";
import { formatDuration, formatTaken } from "@dokument/core";
import type { ColorToken } from "@dokument/tokens";
import { useInfiniteQuery, useQuery, useQueryClient } from "@tanstack/react-query";
import * as Clipboard from "expo-clipboard";
import { router, useLocalSearchParams } from "expo-router";
import { useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { ActivityIndicator, Linking, Pressable, ScrollView, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { useFormat } from "../../../src/state/format";
import {
  allText,
  changeTags,
  fileDetailsQuery,
  fileTagsQuery,
  fileTextPage,
  fileTextQuery,
  invalidateTags,
  keepForever,
  processingQuery,
  restoreVersion,
  retryJob,
  tagSuggestionsQuery,
  versionsQuery,
} from "../../../src/state/info";
import { invalidateFolders, kindOfNode, nodeQuery, sessionQuery } from "../../../src/state/queries";
import { canEdit } from "../../../src/state/changes";
import { accessQuery } from "../../../src/state/sharing";
import { useTheme } from "../../../src/state/theme";
import { Button, IconButton } from "../../../src/ui/Button";
import { EmptyState } from "../../../src/ui/EmptyState";
import { Thumbnail } from "../../../src/ui/FileList";
import { Icon, type IconName } from "../../../src/ui/Icon";
import { Notice } from "../../../src/ui/Notice";
import { Sheet, SheetAction, useKept } from "../../../src/ui/Sheet";
import { toast } from "../../../src/ui/Snackbar";
import { Text } from "../../../src/ui/Text";
import { TextField } from "../../../src/ui/TextField";
import { TopBar } from "../../../src/ui/TopBar";

type TFunction = ReturnType<typeof useTranslation>["t"];

/**
 * A file's or folder's info, full screen (docs/design.md §15.3), from the viewer's top bar and every item's sheet:
 * what the file says about itself, its tags, the start of its text, where the worker is with it, its versions, and
 * where it is and who has it. Details are read-only here; editing them stays on the web.
 */
export default function InfoScreen() {
  const { id } = useLocalSearchParams<{ id: string }>();
  const { t } = useTranslation();
  const theme = useTheme();
  const insets = useSafeAreaInsets();
  const details = useQuery(nodeQuery(id));
  const node = details.data?.node;

  return (
    <View className="flex-1 bg-surface">
      <TopBar title={t("details.tabs.info")} back />
      {!node ? (
        details.isError ? (
          <EmptyState icon="error" title={t("errors.generic")} />
        ) : (
          <View className="flex-1 items-center justify-center">
            <ActivityIndicator color={theme.color("primary")} />
          </View>
        )
      ) : (
        <ScrollView contentContainerStyle={{ paddingBottom: insets.bottom + 24 }}>
          <Header node={node} />
          {node.kind === "file" ? (
            <>
              <DetailsSection node={node} />
              <TextSection node={node} />
              <TagsSection node={node} />
              <ProcessingSection node={node} />
              <VersionsSection node={node} />
            </>
          ) : null}
          <FileSection node={node} path={details.data?.path ?? []} />
        </ScrollView>
      )}
    </View>
  );
}

function Header({ node }: { node: NodeDto }) {
  const { t } = useTranslation();
  const format = useFormat();
  const kind = kindOfNode(node);
  const meta =
    node.kind === "folder"
      ? t("kinds.folder")
      : t("mobile.item.meta", { kind: t(`kinds.${kind}`), size: format.size(node.size ?? 0) });
  return (
    <View className="flex-row items-center gap-4 px-4 pb-2 pt-4">
      <Thumbnail node={node} size="small" className="h-14 w-14 rounded-md" />
      <View className="min-w-0 flex-1">
        <Text variant="title-md" numberOfLines={2} className="text-on-surface">
          {node.name}
        </Text>
        <Text variant="body-sm" className="text-on-surface-variant">
          {meta}
        </Text>
      </View>
    </View>
  );
}

function Section({
  title,
  action,
  children,
}: {
  title: string;
  action?: ReactNode;
  children: ReactNode;
}) {
  return (
    <View className="px-4 pt-5">
      <View className="min-h-touch flex-row items-center">
        <Text variant="title-sm" accessibilityRole="header" className="flex-1 text-on-surface">
          {title}
        </Text>
        {action}
      </View>
      {children}
    </View>
  );
}

function Row({
  label,
  children,
  onPress,
}: {
  label: string;
  children: ReactNode;
  onPress?: () => void;
}) {
  const value = (
    <Text variant="body-md" className={`flex-1 ${onPress ? "text-primary" : "text-on-surface"}`}>
      {children}
    </Text>
  );
  return (
    <View accessible={!onPress} className="flex-row gap-4 py-2">
      <Text variant="body-md" className="w-28 text-on-surface-variant">
        {label}
      </Text>
      {onPress ? (
        <Pressable
          accessibilityRole="link"
          accessibilityLabel={`${label}, ${String(children)}`}
          onPress={onPress}
          className="flex-1"
        >
          {value}
        </Pressable>
      ) : (
        value
      )}
    </View>
  );
}

/** What the file says about itself (§13.5), read-only. */
function DetailsSection({ node }: { node: NodeDto }) {
  const { t, i18n } = useTranslation();
  const details = useQuery(fileDetailsQuery(node.id, node.currentVersionId)).data;
  if (!details) return null;
  const rows = detailRows(t, i18n.language, details);
  return (
    <Section title={t("fileInfo.details")}>
      {rows.length === 0 ? (
        <Text variant="body-md" className="pb-2 text-on-surface-variant">
          {details.reading ? t("fileInfo.readingDetails") : t("fileInfo.noDetails")}
        </Text>
      ) : (
        rows.map((row) => (
          <Row key={row.label} label={row.label} onPress={row.onPress}>
            {row.value}
          </Row>
        ))
      )}
    </Section>
  );
}

function detailRows(t: TFunction, language: string, details: FileDetailsDto) {
  const rows: { label: string; value: string; onPress?: () => void }[] = [];
  const add = (key: string, value: string | null | undefined, onPress?: () => void) => {
    if (value) rows.push({ label: t(`fileInfo.fields.${key}`), value, onPress });
  };
  add("title", details.title.value);
  add("description", details.description.value);
  add("taken", details.taken.value ? formatTaken(t, language, details.taken.value) : null);
  add("camera", details.camera);
  add("lens", details.lens);
  add(
    "dimensions",
    details.width && details.height
      ? t("fileInfo.pixels", { width: details.width, height: details.height })
      : null,
  );
  add("duration", details.durationSeconds != null ? formatDuration(details.durationSeconds) : null);
  add("pages", details.pageCount != null ? String(details.pageCount) : null);
  const place = details.place;
  if (place) {
    add(
      "place",
      `${place.latitude.toFixed(4)}, ${place.longitude.toFixed(4)}`,
      () =>
        void Linking.openURL(
          `https://www.openstreetmap.org/?mlat=${place.latitude}&mlon=${place.longitude}#map=16/${place.latitude}/${place.longitude}`,
        ),
    );
  }
  for (const field of details.fields) add(field.key, field.value);
  return rows;
}

/** The start of the file's text, with Copy all text and the rest on request (§11). */
function TextSection({ node }: { node: NodeDto }) {
  const { t } = useTranslation();
  const theme = useTheme();
  const start = useQuery(fileTextQuery(node.id, node.currentVersionId)).data;
  const [all, setAll] = useState(false);
  const [copying, setCopying] = useState(false);
  const rest = useInfiniteQuery({
    queryKey: ["fileText", node.id, node.currentVersionId, "all"],
    queryFn: ({ pageParam }) => fileTextPage(node.id, pageParam, 20),
    initialPageParam: 1,
    getNextPageParam: (last: FileTextDto) => last.nextFrom ?? undefined,
    enabled: all,
  });
  if (!start || start.state === "unsupported") return null;

  const copy = async () => {
    setCopying(true);
    try {
      await Clipboard.setStringAsync(await allText(node.id));
      toast(t("fileInfo.copied"));
    } catch {
      toast(t("errors.generic"));
    }
    setCopying(false);
  };
  const total = Math.max(start.pageCount ?? 0, start.maxPage);
  const pages = all && rest.data ? rest.data.pages.flatMap((page) => page.pages) : start.pages;
  const sources = [...new Set(start.pages.map((page) => page.source))];
  const summary = [
    sources.length === 1 ? t(`fileInfo.sources.${sources[0]!}`) : null,
    total > 1 ? t("fileInfo.pageCount", { count: total }) : null,
    t("fileInfo.words", { count: start.words }),
  ]
    .filter(Boolean)
    .join(" · ");
  const hasText = start.state === "ready" && start.pages.length > 0;

  return (
    <Section
      title={t("fileInfo.text")}
      action={
        hasText ? (
          <Button
            variant="text"
            icon="content_copy"
            label={t("fileInfo.copyAll")}
            busy={copying}
            onPress={() => void copy()}
          />
        ) : undefined
      }
    >
      {!hasText ? (
        <Text variant="body-md" className="pb-2 text-on-surface-variant">
          {start.state === "reading"
            ? t("fileInfo.readingText")
            : start.state === "unreadable"
              ? t("fileInfo.unreadableText")
              : start.ocrSkipped
                ? t("fileInfo.ocrOff")
                : t("fileInfo.noText")}
        </Text>
      ) : (
        <>
          <Text variant="body-sm" className="pb-2 text-on-surface-variant">
            {summary}
          </Text>
          <View className="gap-4 rounded-md bg-surface-container p-3">
            {pages.map((page) => (
              <View key={page.pageNo} className="gap-0.5">
                {total > 1 ? (
                  <Text variant="label-md" className="text-on-surface-variant">
                    {t(kindOfNode(node) === "slides" ? "fileInfo.slide" : "fileInfo.page", {
                      number: page.pageNo,
                    })}
                  </Text>
                ) : null}
                <Text
                  variant="body-md"
                  selectable
                  numberOfLines={all ? undefined : 8}
                  className="text-on-surface"
                >
                  {page.text}
                </Text>
              </View>
            ))}
            {all && rest.isFetching ? <ActivityIndicator color={theme.color("primary")} /> : null}
          </View>
          {!all &&
          (start.nextFrom != null || start.pages.some((page) => page.text.length > 400)) ? (
            <View className="items-center pt-3">
              <Button
                variant="tonal"
                label={
                  total > 1
                    ? t("fileInfo.showAllPages", { count: total })
                    : t("fileInfo.showAllText")
                }
                onPress={() => setAll(true)}
              />
            </View>
          ) : all && rest.hasNextPage ? (
            <View className="items-center pt-3">
              <Button
                variant="tonal"
                label={t("common.more")}
                busy={rest.isFetchingNextPage}
                onPress={() => void rest.fetchNextPage()}
              />
            </View>
          ) : null}
        </>
      )}
    </Section>
  );
}

/** Tags as on the web (§13.4): editors add and remove them; automatic ones show a spark. Tapping one searches for it. */
function TagsSection({ node }: { node: NodeDto }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const tags = useQuery(fileTagsQuery(node.id));
  // The add sheet stays mounted while it slides away; a new key starts it empty next time.
  const [adding, setAdding] = useState({ key: 0, open: false });
  const editable = canEdit(node.access);
  const shown: Tag[] = tags.data?.tags ?? node.tags;

  const change = async (edit: { add?: string[]; remove?: string[] }, done: string) => {
    try {
      await changeTags([node.id], edit);
      toast(done, {
        label: t("common.undo"),
        onPress: () =>
          void changeTags([node.id], { add: edit.remove, remove: edit.add })
            .catch(() => toast(t("errors.generic")))
            .finally(() => void invalidateTags(queryClient)),
      });
    } catch {
      toast(t("errors.generic"));
    }
    await invalidateTags(queryClient);
  };

  return (
    <Section title={t("tags.title")}>
      <View className="flex-row flex-wrap gap-2 pb-1">
        {shown.map((tag) => (
          <TagChip
            key={tag.name}
            tag={tag}
            onOpen={() => router.push({ pathname: "/find", params: { tag: tag.name } })}
            onRemove={
              editable
                ? () =>
                    void change(
                      { remove: [tag.name] },
                      t("toasts.tagRemoved", { tag: tag.name, name: node.name }),
                    )
                : undefined
            }
          />
        ))}
        {editable ? (
          <Pressable
            accessibilityRole="button"
            onPress={() => setAdding((a) => ({ key: a.key + 1, open: true }))}
            className="h-8 flex-row items-center gap-1 rounded-sm border border-dashed border-outline px-3 active:bg-surface-container-high"
          >
            <Icon name="add" size={18} color="primary" />
            <Text variant="label-lg" className="text-primary">
              {t("tags.add")}
            </Text>
          </Pressable>
        ) : null}
      </View>
      {!editable ? (
        <Text variant="body-sm" className="pt-1 text-on-surface-variant">
          {shown.length === 0 ? t("tags.readOnly") : t("mobile.info.tagsReadOnly")}
        </Text>
      ) : null}
      {adding.key > 0 ? (
        <AddTagSheet
          key={adding.key}
          open={adding.open}
          existing={shown.map((tag) => tag.name.toLowerCase())}
          onClose={() => setAdding((a) => ({ ...a, open: false }))}
          onAdd={(names) => {
            setAdding((a) => ({ ...a, open: false }));
            void change({ add: names }, t("toasts.tagsChanged", { count: 1 }));
          }}
        />
      ) : null}
    </Section>
  );
}

function TagChip({
  tag,
  onOpen,
  onRemove,
}: {
  tag: Tag;
  onOpen: () => void;
  onRemove?: () => void;
}) {
  const { t } = useTranslation();
  const automatic = tag.source !== "user";
  const note =
    tag.source === "ml"
      ? t("tags.byMl")
      : tag.source === "rule"
        ? t("tags.byRule", { name: tag.name })
        : null;
  return (
    <View
      className={`h-8 flex-row items-center rounded-sm ${automatic ? "border border-outline-variant bg-surface" : "bg-secondary-container"}`}
    >
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={t("tags.search", { name: tag.name })}
        accessibilityHint={note ?? undefined}
        onPress={onOpen}
        className={`h-8 flex-row items-center gap-1 ${onRemove ? "pl-3 pr-1" : "px-3"}`}
      >
        {automatic ? <Icon name="auto_awesome" size={16} /> : null}
        <Text
          variant="label-lg"
          className={automatic ? "text-on-surface-variant" : "text-on-secondary-container"}
        >
          {tag.name}
        </Text>
      </Pressable>
      {onRemove ? (
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t("tags.remove", { name: tag.name })}
          onPress={onRemove}
          hitSlop={6}
          className="h-8 w-8 items-center justify-center rounded-sm active:bg-surface-container-highest"
        >
          <Icon
            name="close"
            size={16}
            color={automatic ? "on-surface-variant" : "on-secondary-container"}
          />
        </Pressable>
      ) : null}
    </View>
  );
}

/** Type a tag, or several separated by commas, or pick one already used. */
function AddTagSheet({
  open,
  existing,
  onClose,
  onAdd,
}: {
  open: boolean;
  existing: string[];
  onClose: () => void;
  onAdd: (names: string[]) => void;
}) {
  const { t } = useTranslation();
  const [text, setText] = useState("");
  const typed = text.split(",").at(-1)?.trim() ?? "";
  const suggestions = useQuery(tagSuggestionsQuery(typed)).data ?? [];
  const names = text
    .split(",")
    .map((name) => name.trim())
    .filter(Boolean);
  const tooLong = names.some((name) => name.length > 50);
  const offered = suggestions
    .filter((tag) => !existing.includes(tag.name.toLowerCase()))
    .slice(0, 6);

  return (
    <Sheet open={open} onClose={onClose} title={t("tags.add")}>
      <View className="mx-5 gap-3">
        <TextField
          label={t("tags.addLabel")}
          hint={t("mobile.info.addTagHint")}
          error={tooLong ? t("tags.tooLong") : null}
          value={text}
          onChangeText={setText}
          autoFocus
          autoCapitalize="none"
          returnKeyType="done"
          onSubmitEditing={() => names.length && !tooLong && onAdd(names)}
        />
        {offered.length > 0 ? (
          <View className="flex-row flex-wrap gap-2">
            {offered.map((tag) => (
              <Pressable
                key={tag.name}
                accessibilityRole="button"
                accessibilityLabel={`${tag.name}, ${t("tags.files", { count: tag.files })}`}
                onPress={() => onAdd([...names.slice(0, typed ? -1 : undefined), tag.name])}
                className="h-8 justify-center rounded-sm border border-outline px-3 active:bg-surface-container-high"
              >
                <Text variant="label-lg" className="text-on-surface-variant">
                  {tag.name}
                </Text>
              </Pressable>
            ))}
          </View>
        ) : null}
        <View className="flex-row justify-end gap-2">
          <Button variant="text" label={t("common.cancel")} onPress={onClose} />
          <Button
            label={t("tags.add")}
            disabled={!names.length || tooLong}
            onPress={() => onAdd(names)}
          />
        </View>
      </View>
    </Sheet>
  );
}

const stepIcons: Record<
  ProcessingStep["state"],
  { name: IconName; filled?: boolean; color: ColorToken }
> = {
  notStarted: { name: "schedule", color: "on-surface-variant" },
  waiting: { name: "hourglass_top", color: "on-surface-variant" },
  paused: { name: "pause_circle", color: "warning" },
  running: { name: "progress_activity", color: "primary" },
  done: { name: "check_circle", filled: true, color: "success" },
  nothingFound: { name: "check_circle", filled: true, color: "success" },
  skipped: { name: "block", color: "on-surface-variant" },
  unsupported: { name: "remove_circle_outline", color: "on-surface-variant" },
  unreadable: { name: "error_outline", color: "on-surface-variant" },
  failed: { name: "error", filled: true, color: "error" },
  onOpen: { name: "play_circle", color: "on-surface-variant" },
  off: { name: "block", color: "on-surface-variant" },
};

/** States with nothing more to happen. */
const settled: ProcessingStep["state"][] = [
  "done",
  "nothingFound",
  "skipped",
  "unsupported",
  "unreadable",
  "onOpen",
  "off",
];

/** Where the worker is with each step (§10.1), as on the web; folded to one line once all are settled. */
function ProcessingSection({ node }: { node: NodeDto }) {
  const { t } = useTranslation();
  const theme = useTheme();
  const format = useFormat();
  const queryClient = useQueryClient();
  const steps = useQuery(processingQuery(node.id, node.currentVersionId)).data?.steps ?? [];
  const admin = useQuery(sessionQuery).data?.user.role === "admin";
  const [expanded, setExpanded] = useState(false);
  if (steps.length === 0) return null;
  const when = (at?: string | null) => (at ? format.date(at, true) : "");

  if (!expanded && steps.every((step) => settled.includes(step.state))) {
    return (
      <View className="px-4 pt-5">
        <Pressable
          accessibilityRole="button"
          accessibilityState={{ expanded: false }}
          onPress={() => setExpanded(true)}
          className="min-h-touch flex-row items-center gap-3 rounded-sm active:bg-surface-container-high"
        >
          <Icon name="check_circle" filled size={20} color="success" />
          <Text variant="title-sm" className="flex-1 text-on-surface">
            {t("processing.title")}
            <Text variant="body-sm" className="text-on-surface-variant">
              {` · ${t("processing.allDone")}`}
            </Text>
          </Text>
          <Icon name="expand_more" />
        </Pressable>
      </View>
    );
  }

  const describe = (s: ProcessingStep) => {
    switch (s.state) {
      case "running":
        if (s.progress != null)
          return t("processing.state.runningProgress", { percent: s.progress });
        return s.at
          ? t("processing.state.runningSince", { time: when(s.at) })
          : t("processing.state.running");
      case "unreadable":
        return t(
          s.step === "transcode" ? "processing.state.unconvertible" : "processing.state.unreadable",
        );
      case "waiting":
        return s.ahead
          ? t("processing.state.waiting", { count: s.ahead })
          : t("processing.state.next");
      case "failed":
        return s.error && s.at
          ? t("processing.state.gaveUp", { count: s.attempts ?? 0, time: when(s.at) })
          : t("processing.state.failed");
      case "done":
      case "nothingFound":
        return t(`processing.state.${s.state}`, { time: when(s.at) });
      default:
        return t(`processing.state.${s.state}`);
    }
  };
  const retry = async (jobId: string) => {
    try {
      await retryJob(jobId);
      await queryClient.invalidateQueries({ queryKey: ["processing", node.id] });
      toast(t("processing.retried"));
    } catch {
      toast(t("errors.generic"));
    }
  };

  return (
    <Section title={t("processing.title")}>
      <View className="gap-3 pb-1">
        {steps.map((s) => {
          const icon = stepIcons[s.state];
          const searchWaits =
            s.step === "text" && ["waiting", "paused", "running", "notStarted"].includes(s.state);
          return (
            <View key={s.step} className="flex-row gap-3">
              <View className="h-6 w-5 items-center justify-center">
                {s.state === "running" ? (
                  <ActivityIndicator size="small" color={theme.color("primary")} />
                ) : (
                  <Icon name={icon.name} filled={icon.filled} size={20} color={icon.color} />
                )}
              </View>
              <View accessible className="min-w-0 flex-1 gap-0.5">
                <Text variant="body-md" className="text-on-surface">
                  {t(`processing.steps.${s.step}`)}
                </Text>
                <Text variant="body-sm" className="text-on-surface-variant">
                  {describe(s)}
                  {searchWaits ? ` ${t("processing.searchWaits")}` : ""}
                </Text>
                {(s.state === "failed" || s.state === "unreadable") && s.error ? (
                  <Text
                    variant="body-sm"
                    selectable
                    className="mt-1 rounded-xs bg-surface-container-high px-1.5 py-1 text-on-surface-variant"
                  >
                    {s.error}
                  </Text>
                ) : null}
              </View>
              {s.state === "failed" && s.jobId && admin ? (
                <Button
                  variant="text"
                  icon="replay"
                  label={t("processing.retry")}
                  onPress={() => void retry(s.jobId!)}
                />
              ) : null}
            </View>
          );
        })}
      </View>
    </Section>
  );
}

/** Whole days until `at`, for "Removed in N days". */
const daysUntil = (at: string) =>
  Math.max(0, Math.ceil((Date.parse(at) - Date.now()) / 86_400_000));

/** Its versions (§9): editors make an older one current; the owner keeps one out of retention. */
function VersionsSection({ node }: { node: NodeDto }) {
  const { t } = useTranslation();
  const format = useFormat();
  const queryClient = useQueryClient();
  const versions = useQuery(versionsQuery(node.id)).data;
  const [chosen, setChosen] = useState<VersionDto | null>(null);
  const shown = useKept(chosen);
  if (!versions?.length) return null;
  const canRestore = canEdit(node.access);
  const canKeep = node.access === "owner";

  const act = async (version: VersionDto, action: "restore" | "keep" | "unkeep") => {
    setChosen(null);
    try {
      if (action === "restore") {
        await restoreVersion(node.id, version.id);
        toast(t("toasts.versionRestored", { number: version.number }));
      } else {
        await keepForever(node.id, version.id, action === "keep");
        toast(
          t(action === "keep" ? "toasts.versionKept" : "toasts.versionUnkept", {
            number: version.number,
          }),
        );
      }
    } catch {
      toast(t("errors.generic"));
    }
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ["versions", node.id] }),
      queryClient.invalidateQueries({ queryKey: ["node", node.id] }),
      invalidateFolders(queryClient, [node.parentId ?? null]),
    ]);
  };

  return (
    <Section title={t("details.tabs.versions")}>
      {versions.map((version) => {
        const removedIn =
          !version.current && !version.keepForever && version.purgeAt
            ? daysUntil(version.purgeAt)
            : null;
        const actionable = (!version.current && canRestore) || canKeep;
        return (
          <View
            key={version.id}
            className="flex-row items-center gap-3 border-b border-outline-variant py-2 last:border-b-0"
          >
            <View
              className={`h-6 w-6 items-center justify-center rounded-full ${version.current ? "bg-primary" : "bg-surface-container-highest"}`}
            >
              <Text
                variant="label-sm"
                className={version.current ? "text-on-primary" : "text-on-surface-variant"}
              >
                {version.number}
              </Text>
            </View>
            <View accessible className="min-w-0 flex-1 gap-0.5">
              <View className="flex-row flex-wrap items-center gap-2">
                <Text variant="body-md" className="font-medium text-on-surface">
                  {t("versions.version", { number: version.number })}
                </Text>
                {version.current ? (
                  <View className="rounded-xs bg-primary-container px-1.5">
                    <Text variant="label-sm" className="uppercase text-on-primary-container">
                      {t("versions.current")}
                    </Text>
                  </View>
                ) : null}
                {version.keepForever ? (
                  <View className="flex-row items-center gap-0.5 rounded-xs bg-secondary-container px-1.5">
                    <Icon name="keep" size={14} filled color="on-secondary-container" />
                    <Text variant="label-sm" className="uppercase text-on-secondary-container">
                      {t("versions.kept")}
                    </Text>
                  </View>
                ) : null}
              </View>
              <Text variant="body-sm" className="text-on-surface-variant">
                {[
                  version.uploadedByName,
                  format.date(version.uploadedAt),
                  format.size(version.size),
                ].join(" · ")}
              </Text>
              {removedIn != null ? (
                <Text variant="body-sm" className="text-warning">
                  {t("versions.removedIn", { count: removedIn })}
                </Text>
              ) : null}
            </View>
            {actionable ? (
              <IconButton
                icon="more_vert"
                label={t("versions.actionsFor", { number: version.number })}
                onPress={() => setChosen(version)}
              />
            ) : null}
          </View>
        );
      })}
      <View className="flex-row gap-2 py-3">
        <Icon name="info" size={18} />
        <Text variant="body-sm" className="flex-1 text-on-surface-variant">
          {t("versions.policy")}
        </Text>
      </View>
      <Sheet
        open={!!chosen}
        onClose={() => setChosen(null)}
        title={shown ? t("versions.version", { number: shown.number }) : undefined}
      >
        {shown && !shown.current && canRestore ? (
          <SheetAction
            icon={<Icon name="history" />}
            label={t("versions.restore")}
            onPress={() => void act(shown, "restore")}
          />
        ) : null}
        {shown && canKeep ? (
          <SheetAction
            icon={<Icon name={shown.keepForever ? "keep_off" : "keep"} />}
            label={t(shown.keepForever ? "versions.unkeep" : "versions.keep")}
            onPress={() => void act(shown, shown.keepForever ? "unkeep" : "keep")}
          />
        ) : null}
      </Sheet>
    </Section>
  );
}

/** Where it is, whose it is, when it changed, and who it's shared with. */
function FileSection({ node, path }: { node: NodeDto; path: PathEntry[] }) {
  const { t } = useTranslation();
  const format = useFormat();
  const me = useQuery(sessionQuery).data?.user.id;
  const access = useQuery(accessQuery(node.id)).data;
  const mine = node.ownerId === me;
  const root = node.access === "owner" ? t("nav.myFiles") : t("nav.sharedWithMe");
  const others = access
    ? access.people.filter((entry) => entry.person.id !== access.owner.id).length
    : null;
  return (
    <Section title={t(node.kind === "folder" ? "kinds.folder" : "mobile.info.file")}>
      <Row label={t("details.location")}>
        {[root, ...path.map((entry) => entry.name)].join(" / ")}
      </Row>
      <Row label={t("details.owner")}>{mine ? t("mobile.info.you") : node.ownerName}</Row>
      <Row label={t("details.modified")}>{format.date(node.modifiedAt)}</Row>
      <Row label={t("details.created")}>{format.date(node.createdAt)}</Row>
      {others != null ? (
        <Row label={t("mobile.info.shared")}>
          {others === 0
            ? t("mobile.info.notShared")
            : t("mobile.info.sharedWith", { count: others })}
        </Row>
      ) : null}
      {node.kind === "file" ? (
        <View className="pt-3">
          <Notice icon="info" text={t("mobile.info.onTheWeb")} />
        </View>
      ) : null}
    </Section>
  );
}
