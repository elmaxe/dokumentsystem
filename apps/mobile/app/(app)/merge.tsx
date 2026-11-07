import type { NodeDto } from "@dokument/api";
import { stemOf, withPdfExtension } from "@dokument/core";
import { useQueries } from "@tanstack/react-query";
import { router, useLocalSearchParams } from "expo-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { ActivityIndicator, ScrollView, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { pdf, pdfProblem, useStartedPdf } from "../../src/state/pdf";
import { nodeQuery } from "../../src/state/queries";
import { useTheme } from "../../src/state/theme";
import { Button, IconButton } from "../../src/ui/Button";
import { FileTypeIcon } from "../../src/ui/FileTypeIcon";
import { Notice } from "../../src/ui/Notice";
import { SwitchRow } from "../../src/ui/SettingsRows";
import { Text } from "../../src/ui/Text";
import { TextField } from "../../src/ui/TextField";
import { TopBar } from "../../src/ui/TopBar";

/** The most PDFs and pages one merge takes, as the server says (docs/design.md §8.4). */
const maxFiles = 100;
const maxPages = 2000;

/**
 * Merges the selected PDFs into one new file (docs/design.md §8.4): the person puts them in order, names the new file
 * and may move the originals to the trash. The new file goes next to the first, and its progress shows in the folder.
 */
export default function Merge() {
  const { t } = useTranslation();
  const theme = useTheme();
  const insets = useSafeAreaInsets();
  const started = useStartedPdf();
  const { ids = "" } = useLocalSearchParams<{ ids?: string }>();
  const queries = useQueries({
    queries: ids
      .split(",")
      .filter(Boolean)
      .map((id) => nodeQuery(id)),
  });
  const loaded = queries.every((query) => query.data);
  const [order, setOrder] = useState<NodeDto[] | null>(null);
  const [typed, setTyped] = useState<string | null>(null);
  const [trash, setTrash] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Starts from the order the files were selected in, once they're all fetched.
  const list = order ?? (loaded ? queries.map((query) => query.data!.node) : []);
  const name = typed ?? (list[0] ? t("pdf.merge.defaultName", { stem: stemOf(list[0].name) }) : "");
  const pages = list.reduce((sum, node) => sum + (node.pdf?.pageCount ?? 0), 0);
  const tooMany = list.length > maxFiles || pages > maxPages;
  const move = (from: number, to: number) => {
    if (to < 0 || to >= list.length) return;
    const next = [...list];
    const [node] = next.splice(from, 1);
    next.splice(to, 0, node!);
    setOrder(next);
  };

  const submit = async () => {
    const first = list[0];
    if (!first || list.length < 2 || !name.trim() || tooMany) return;
    setBusy(true);
    setError(null);
    try {
      started(
        await pdf.merge({
          ids: list.map((node) => node.id),
          folderId: first.parentId ?? null,
          name: withPdfExtension(name),
          trashOriginals: trash,
        }),
      );
      router.back();
    } catch (problem) {
      setError(pdfProblem(t, problem));
      setBusy(false);
    }
  };

  return (
    <View className="flex-1 bg-surface">
      <TopBar title={t("pdf.merge.title")} back />
      {!loaded ? (
        <View className="flex-1 items-center justify-center">
          {queries.some((query) => query.isError) ? (
            <View className="p-4">
              <Notice tone="error" icon="error" text={t("errors.generic")} />
            </View>
          ) : (
            <ActivityIndicator color={theme.color("primary")} />
          )}
        </View>
      ) : (
        <ScrollView
          contentContainerStyle={{ paddingBottom: insets.bottom + 24 }}
          contentContainerClassName="gap-4 p-4"
          keyboardShouldPersistTaps="handled"
        >
          <Text variant="body-md" className="text-on-surface-variant">
            {t("mobile.pdf.mergeIntro")}
          </Text>
          <View className="overflow-hidden rounded-md bg-surface-container-lowest">
            {list.map((node, index) => (
              <View
                key={node.id}
                className={`min-h-16 flex-row items-center gap-3 py-2 pl-4 pr-1 ${index > 0 ? "border-t border-outline-variant" : ""}`}
              >
                <Text variant="body-sm" className="w-5 text-right text-on-surface-variant">
                  {index + 1}
                </Text>
                <FileTypeIcon kind="pdf" />
                <View className="min-w-0 flex-1">
                  <Text numberOfLines={1} className="text-on-surface">
                    {node.name}
                  </Text>
                  <Text variant="body-sm" className="text-on-surface-variant">
                    {t("pdf.pages", { count: node.pdf?.pageCount ?? 0 })}
                  </Text>
                </View>
                <IconButton
                  icon="arrow_upward"
                  label={t("mobile.pdf.moveUp", { name: node.name })}
                  disabled={index === 0}
                  onPress={() => move(index, index - 1)}
                  compact
                />
                <IconButton
                  icon="arrow_downward"
                  label={t("mobile.pdf.moveDown", { name: node.name })}
                  disabled={index === list.length - 1}
                  onPress={() => move(index, index + 1)}
                  compact
                />
                <IconButton
                  icon="close"
                  label={t("mobile.pdf.leaveOut", { name: node.name })}
                  disabled={list.length <= 2}
                  onPress={() => setOrder(list.filter((other) => other.id !== node.id))}
                  compact
                />
              </View>
            ))}
          </View>
          <Text variant="body-md" className="text-on-surface-variant">
            {tooMany
              ? t("pdf.merge.tooMany", { files: maxFiles, pages: maxPages })
              : t("pdf.merge.total", { count: pages })}
          </Text>
          <TextField
            label={t("pdf.merge.name")}
            value={name}
            onChangeText={(value) => {
              setTyped(value);
              setError(null);
            }}
            error={error}
            maxLength={255}
            returnKeyType="done"
          />
          <SwitchRow label={t("pdf.merge.trash")} value={trash} onChange={setTrash} />
          <Notice icon="info" text={t("pdf.notKept")} />
          <Button
            label={t("pdf.merge.submit")}
            icon="call_merge"
            busy={busy}
            disabled={list.length < 2 || !name.trim() || tooMany}
            onPress={() => void submit()}
          />
        </ScrollView>
      )}
    </View>
  );
}
