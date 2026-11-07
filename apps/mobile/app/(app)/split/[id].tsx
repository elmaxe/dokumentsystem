import type { PageSheets } from "@dokument/api";
import {
  everyCuts,
  parsePages,
  partsFromCuts,
  rangesText,
  stemOf,
  withPdfExtension,
  type PageList,
  type PagesProblem,
} from "@dokument/core";
import { useQuery } from "@tanstack/react-query";
import { Image } from "expo-image";
import { router, useLocalSearchParams } from "expo-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { ActivityIndicator, Pressable, ScrollView, useWindowDimensions, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import {
  pageSheetSource,
  pageSheetsQuery,
  pdf,
  pdfProblem,
  useStartedPdf,
} from "../../../src/state/pdf";
import { nodeQuery } from "../../../src/state/queries";
import { useTheme } from "../../../src/state/theme";
import { Button, IconButton } from "../../../src/ui/Button";
import { Icon } from "../../../src/ui/Icon";
import { Notice } from "../../../src/ui/Notice";
import { Segmented, SwitchRow } from "../../../src/ui/SettingsRows";
import { Sheet, SheetAction } from "../../../src/ui/Sheet";
import { Text } from "../../../src/ui/Text";
import { TextField } from "../../../src/ui/TextField";
import { TopBar } from "../../../src/ui/TopBar";

/** The most parts one split makes, as the server says (docs/design.md §8.4). */
const maxParts = 500;
const columns = 3;
const gap = 12;

type Mode = "pages" | "ranges";
type RangePart = { key: number; pages: string; name: string };

/** One page's image: the sheet it's on, moved so only its cell shows. */
function PageImage({
  id,
  versionId,
  sheets,
  page,
  width,
}: {
  id: string;
  versionId: string | null;
  sheets: PageSheets;
  page: number;
  width: number;
}) {
  const index = page - 1;
  const sheet = Math.floor(index / sheets.perSheet) + 1;
  const cell = index % sheets.perSheet;
  const onSheet = Math.min(sheets.perSheet, sheets.pageCount - (sheet - 1) * sheets.perSheet);
  const across = Math.min(sheets.columns, onSheet);
  const rows = Math.ceil(onSheet / across);
  const height = (sheets.cellHeight / sheets.cellWidth) * width;
  return (
    <Image
      source={pageSheetSource(id, sheet, versionId)}
      cachePolicy="disk"
      contentFit="fill"
      accessible={false}
      style={{
        position: "absolute",
        width: across * width,
        height: rows * height,
        left: -(cell % across) * width,
        top: -Math.floor(cell / across) * height,
      }}
    />
  );
}

/**
 * Splits a PDF into new files (docs/design.md §8.4), as on the web: a tap on a page cuts after it or leaves it out,
 * presets cut every page or every two, and Ranges takes typed pages. Each new file gets a name below the pages.
 */
export default function Split() {
  const { t } = useTranslation();
  const theme = useTheme();
  const insets = useSafeAreaInsets();
  const { width: screen } = useWindowDimensions();
  const started = useStartedPdf();
  const { id } = useLocalSearchParams<{ id: string }>();
  const details = useQuery(nodeQuery(id));
  const node = details.data?.node;
  const versionId = node?.currentVersionId ?? null;
  const sheets = useQuery({ ...pageSheetsQuery(id, versionId), enabled: !!node });
  const pageCount = sheets.data?.pageCount ?? node?.pdf?.pageCount ?? 0;
  const stem = stemOf(node?.name ?? "");
  const width = Math.floor((screen - 32 - gap * (columns - 1)) / columns);
  const height = Math.round((283 / 200) * width);

  const [mode, setMode] = useState<Mode>("pages");
  const [cuts, setCuts] = useState<Set<number>>(() => new Set());
  const [left, setLeft] = useState<Set<number>>(() => new Set());
  const [ranges, setRanges] = useState<RangePart[]>([]);
  const [names, setNames] = useState<Record<string, string>>({});
  const [newFolder, setNewFolder] = useState(false);
  const [folderName, setFolderName] = useState<string | null>(null);
  const [trash, setTrash] = useState(false);
  const [page, setPage] = useState<number | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const defaultName = (pages: PageList) =>
    t("pdf.split.partName", { stem, pages: rangesText(pages, "–"), count: pages.length });
  const problemText = (problem: PagesProblem) => {
    switch (problem.kind) {
      case "empty":
        return t("pdf.split.problems.empty");
      case "invalid":
        return t("pdf.split.problems.invalid", { text: problem.text });
      case "backwards":
        return t("pdf.split.problems.backwards", { text: problem.text });
      case "outside":
        return t("pdf.split.problems.outside", { text: problem.text, count: problem.pageCount });
    }
  };

  const parts =
    mode === "pages"
      ? partsFromCuts(pageCount, cuts, left).map((pages) => {
          const key = rangesText(pages);
          return {
            key,
            pages,
            name: names[key] ?? defaultName(pages),
            problem: undefined as string | undefined,
          };
        })
      : ranges.map((part) => {
          const read = parsePages(part.pages, pageCount);
          return "pages" in read
            ? {
                key: String(part.key),
                pages: read.pages,
                name: part.name || defaultName(read.pages),
                problem: undefined,
              }
            : {
                key: String(part.key),
                pages: [] as PageList,
                name: part.name,
                problem: problemText(read.problem),
              };
        });
  const inPart = new Map<number, number>();
  parts.forEach((part, index) => part.pages.forEach((p) => inPart.has(p) || inPart.set(p, index)));
  // One part with every page would just be a copy.
  const ready =
    (parts.length > 1 || left.size > 0 || mode === "ranges") &&
    parts.length > 0 &&
    parts.length <= maxParts &&
    parts.every((part) => !part.problem && part.name.trim()) &&
    (!newFolder || !!(folderName ?? stem).trim());

  const toggle = (set: Set<number>, value: number) => {
    const next = new Set(set);
    if (!next.delete(value)) next.add(value);
    return next;
  };

  const changeMode = (next: Mode) => {
    if (next === "ranges" && mode === "pages") {
      setRanges(
        parts.map((part, index) => ({
          key: index,
          pages: rangesText(part.pages),
          name: names[part.key] ?? "",
        })),
      );
    }
    setMode(next);
  };

  const submit = async () => {
    if (!node || !ready) return;
    setBusy(true);
    setError(null);
    try {
      started(
        await pdf.split({
          id: node.id,
          parts: parts.map((part) => ({
            name: withPdfExtension(part.name),
            pages: rangesText(part.pages),
          })),
          newFolderName: newFolder ? (folderName ?? stem).trim() : null,
          trashOriginal: trash,
        }),
      );
      router.back();
    } catch (problem) {
      setError(pdfProblem(t, problem));
      setBusy(false);
    }
  };

  if (details.isError || sheets.isError) {
    return (
      <View className="flex-1 bg-surface">
        <TopBar title={t("pdf.split.action")} back />
        <View className="p-4">
          <Notice
            tone="error"
            icon="error"
            text={details.isError ? t("errors.generic") : t("pdf.split.unreadable")}
          />
        </View>
      </View>
    );
  }

  return (
    <View className="flex-1 bg-surface">
      <TopBar title={node ? t("pdf.split.title", { name: node.name }) : ""} back />
      {!node || !sheets.data ? (
        <View className="flex-1 items-center justify-center">
          <ActivityIndicator color={theme.color("primary")} />
        </View>
      ) : (
        <ScrollView
          contentContainerStyle={{ paddingBottom: insets.bottom + 24 }}
          contentContainerClassName="gap-4 p-4"
          keyboardShouldPersistTaps="handled"
        >
          <Segmented
            label={t("mobile.pdf.mode")}
            options={[
              { value: "pages", label: t("pdf.split.modes.pages") },
              { value: "ranges", label: t("pdf.split.modes.ranges") },
            ]}
            value={mode}
            onChange={changeMode}
          />
          {mode === "pages" ? (
            <View className="-mx-2 flex-row flex-wrap">
              <Button
                variant="text"
                label={t("pdf.split.everyPage")}
                onPress={() => setCuts(everyCuts(pageCount, 1))}
              />
              <Button
                variant="text"
                label={t("pdf.split.everyTwo")}
                onPress={() => setCuts(everyCuts(pageCount, 2))}
              />
              {cuts.size > 0 || left.size > 0 ? (
                <Button
                  variant="text"
                  label={t("pdf.split.startOver")}
                  onPress={() => {
                    setCuts(new Set());
                    setLeft(new Set());
                  }}
                />
              ) : null}
            </View>
          ) : null}
          <Text variant="body-md" className="text-on-surface-variant">
            {mode === "pages" ? t("mobile.pdf.pagesHint") : t("pdf.split.rangesHint")}
          </Text>
          {!sheets.data.ready ? (
            <View className="flex-row items-center gap-2">
              <Icon name="hourglass_top" size={18} />
              <Text variant="body-md" className="text-on-surface-variant">
                {t("pdf.split.preparing")}
              </Text>
            </View>
          ) : null}
          <View className="flex-row flex-wrap" style={{ gap }}>
            {Array.from({ length: pageCount }, (_, i) => i + 1).map((p) => {
              const part = inPart.get(p);
              const out = part === undefined;
              return (
                <Pressable
                  key={p}
                  accessibilityRole="button"
                  accessibilityLabel={t("pdf.split.pageMenu", { page: p })}
                  accessibilityHint={
                    out
                      ? t("mobile.pdf.leftOut")
                      : [
                          t("mobile.pdf.inFile", { number: part + 1 }),
                          cuts.has(p) ? t("mobile.pdf.cutAfter") : null,
                        ]
                          .filter(Boolean)
                          .join(", ")
                  }
                  disabled={mode === "ranges"}
                  onPress={() => setPage(p)}
                  className="items-center gap-1"
                >
                  <View
                    style={{ width, height }}
                    className="overflow-hidden rounded-xs bg-surface-container-lowest shadow-elevation-1"
                  >
                    {sheets.data!.ready ? (
                      <PageImage
                        id={id}
                        versionId={versionId}
                        sheets={sheets.data!}
                        page={p}
                        width={width}
                      />
                    ) : (
                      <View className="flex-1 bg-surface-container-high" />
                    )}
                    {out ? (
                      <View className="absolute inset-0 items-center justify-center">
                        {/* The theme's colours take no alpha, so the page is dimmed by a layer of its own. */}
                        <View style={{ opacity: 0.75 }} className="absolute inset-0 bg-surface" />
                        <Icon name="block" size={40} color="on-surface-variant" />
                      </View>
                    ) : (
                      <View
                        className={`absolute left-1 top-1 min-w-6 rounded-full px-1.5 ${part % 2 === 0 ? "bg-primary" : "bg-secondary-container"}`}
                      >
                        <Text
                          variant="label-sm"
                          className={`text-center ${part % 2 === 0 ? "text-on-primary" : "text-on-secondary-container"}`}
                        >
                          {part + 1}
                        </Text>
                      </View>
                    )}
                    {cuts.has(p) && p < pageCount && mode === "pages" ? (
                      <View className="absolute bottom-1 right-1 rounded-full bg-primary p-0.5">
                        <Icon name="content_cut" size={16} color="on-primary" />
                      </View>
                    ) : null}
                  </View>
                  <Text variant="body-sm" className="text-on-surface-variant">
                    {p}
                  </Text>
                </Pressable>
              );
            })}
          </View>

          <Text variant="title-md" accessibilityRole="header" className="mt-2 text-on-surface">
            {t("pdf.split.parts", { count: parts.length })}
          </Text>
          {parts.length > maxParts ? (
            <Notice
              tone="warning"
              icon="warning"
              text={t("pdf.split.tooMany", { count: maxParts })}
            />
          ) : null}
          {parts.length === 0 && mode === "pages" ? (
            <Text variant="body-md" className="text-on-surface-variant">
              {t("pdf.split.nothingLeft")}
            </Text>
          ) : null}
          {parts.slice(0, maxParts).map((part, index) => (
            <View key={part.key} className="gap-2 rounded-md bg-surface-container-lowest p-3">
              <View className="flex-row items-center gap-2">
                <Text variant="title-sm" className="flex-1 text-on-surface">
                  {t("mobile.pdf.fileNumber", { number: index + 1 })}
                </Text>
                {mode === "ranges" ? (
                  <IconButton
                    icon="delete"
                    label={t("pdf.split.removePart", { number: index + 1 })}
                    disabled={ranges.length <= 1}
                    onPress={() =>
                      setRanges((all) => all.filter((r) => String(r.key) !== part.key))
                    }
                    compact
                  />
                ) : (
                  <Text variant="body-sm" className="text-on-surface-variant">
                    {t("pdf.split.partPages", {
                      pages: rangesText(part.pages, "–"),
                      count: part.pages.length,
                    })}
                  </Text>
                )}
              </View>
              <TextField
                label={t("pdf.split.name")}
                value={part.name}
                maxLength={255}
                onChangeText={(value) =>
                  mode === "pages"
                    ? setNames((all) => ({ ...all, [part.key]: value }))
                    : setRanges((all) =>
                        all.map((r) => (String(r.key) === part.key ? { ...r, name: value } : r)),
                      )
                }
              />
              {mode === "ranges" ? (
                <TextField
                  label={t("pdf.split.pages")}
                  value={ranges.find((r) => String(r.key) === part.key)?.pages ?? ""}
                  error={part.problem}
                  hint={t("pdf.split.pagesFieldHint", { count: pageCount })}
                  keyboardType="numbers-and-punctuation"
                  onChangeText={(value) =>
                    setRanges((all) =>
                      all.map((r) => (String(r.key) === part.key ? { ...r, pages: value } : r)),
                    )
                  }
                />
              ) : null}
            </View>
          ))}
          {mode === "ranges" ? (
            <Button
              variant="outlined"
              icon="add"
              label={t("pdf.split.addPart")}
              onPress={() =>
                setRanges((all) => [
                  ...all,
                  { key: Math.max(-1, ...all.map((r) => r.key)) + 1, pages: "", name: "" },
                ])
              }
            />
          ) : null}
          <SwitchRow label={t("pdf.split.newFolder")} value={newFolder} onChange={setNewFolder} />
          {newFolder ? (
            <TextField
              label={t("pdf.split.folderName")}
              value={folderName ?? stem}
              maxLength={255}
              onChangeText={setFolderName}
            />
          ) : null}
          <SwitchRow label={t("pdf.split.trash")} value={trash} onChange={setTrash} />
          <Notice icon="info" text={t("pdf.notKept")} />
          {error ? <Notice tone="error" icon="error" text={error} /> : null}
          <Button
            label={t("pdf.split.submit", { count: parts.length })}
            icon="call_split"
            busy={busy}
            disabled={!ready}
            onPress={() => void submit()}
          />
        </ScrollView>
      )}

      <Sheet
        open={page !== null}
        onClose={() => setPage(null)}
        title={page !== null ? t("pdf.split.pageMenu", { page }) : undefined}
      >
        {page !== null ? (
          <>
            {page < pageCount ? (
              <SheetAction
                icon={<Icon name="content_cut" />}
                label={cuts.has(page) ? t("pdf.split.joinAfter") : t("pdf.split.cutAfter")}
                onPress={() => {
                  setCuts((set) => toggle(set, page));
                  setPage(null);
                }}
              />
            ) : null}
            <SheetAction
              icon={<Icon name={left.has(page) ? "add_circle" : "remove_circle"} />}
              label={left.has(page) ? t("pdf.split.putBack") : t("pdf.split.leaveOut")}
              onPress={() => {
                setLeft((set) => toggle(set, page));
                setPage(null);
              }}
            />
          </>
        ) : null}
      </Sheet>
    </View>
  );
}
