import { folderProblem, kindOf, pickedTemplate } from "@dokument/core";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { router, useLocalSearchParams } from "expo-router";
import { useEffect, useRef, useState, type ComponentRef } from "react";
import { useTranslation } from "react-i18next";
import { Modal, Pressable, ScrollView, View, type TextInput } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { filing, filingPreviewQuery, filingRulesQuery, isTaken } from "../../../src/state/filing";
import { tagSuggestionsQuery } from "../../../src/state/info";
import { useTheme } from "../../../src/state/theme";
import { Button, IconButton } from "../../../src/ui/Button";
import { FileTypeIcon } from "../../../src/ui/FileTypeIcon";
import { FolderPicker } from "../../../src/ui/FolderPicker";
import { Icon, type IconName } from "../../../src/ui/Icon";
import { Notice } from "../../../src/ui/Notice";
import { toast } from "../../../src/ui/Snackbar";
import { Text } from "../../../src/ui/Text";
import { TextField } from "../../../src/ui/TextField";

/**
 * Adds or edits a filing rule (docs/design.md §13.6): the tag, with suggestions from tags the person can see, and the
 * folder, typed as on the web or chosen with the folder picker, with Year and Month to insert the placeholders. Below
 * them, where a couple of the files that have the tag now would go, and whether to file those too.
 */
export default function FilingRuleEditor() {
  const { t } = useTranslation();
  const theme = useTheme();
  const insets = useSafeAreaInsets();
  const queryClient = useQueryClient();
  const { id } = useLocalSearchParams<{ id?: string }>();
  const existing = useQuery(filingRulesQuery).data?.find((rule) => rule.id === id) ?? null;
  const [tag, setTag] = useState(existing?.tag ?? "");
  const [folder, setFolder] = useState(existing?.folder ?? "");
  const [fileExisting, setFileExisting] = useState(!id);
  const [typed, setTyped] = useState({ tag: tag.trim(), folder: folder.trim() });
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [picking, setPicking] = useState(false);
  const folderRef = useRef<ComponentRef<typeof TextInput>>(null);
  const selection = useRef({ start: folder.length, end: folder.length });
  const problem = folder.trim() ? folderProblem(folder) : null;

  useEffect(() => {
    const timer = setTimeout(() => setTyped({ tag: tag.trim(), folder: folder.trim() }), 400);
    return () => clearTimeout(timer);
  }, [tag, folder]);

  const suggestions = useQuery(tagSuggestionsQuery(tag.trim())).data ?? [];
  const preview = useQuery(filingPreviewQuery(typed.tag, typed.folder));
  const files = preview.data?.files ?? 0;
  const examples = !problem && typed.folder ? (preview.data?.examples ?? []) : [];

  const insert = (placeholder: string) => {
    const { start, end } = selection.current;
    const before = folder.slice(0, start);
    const glue = before && !before.endsWith("/") ? "/" : "";
    const caret = (before + glue + placeholder).length;
    setFolder(before + glue + placeholder + folder.slice(end));
    selection.current = { start: caret, end: caret };
  };

  const save = async () => {
    setSaving(true);
    setError(null);
    try {
      const body = {
        tag,
        folder,
        enabled: existing?.enabled ?? true,
        fileExisting: fileExisting && files > 0,
      };
      if (existing) await filing.update(existing.id, body);
      else await filing.create(body);
      await queryClient.invalidateQueries({ queryKey: filingRulesQuery.queryKey });
      toast(t("filing.saved"));
      router.back();
    } catch (failure) {
      setSaving(false);
      setError(isTaken(failure) ? t("filing.taken", { tag: tag.trim() }) : t("errors.generic"));
    }
  };

  const folderError =
    problem === "placeholder"
      ? t("filing.badPlaceholder")
      : problem === "empty"
        ? t("filing.emptyFolder")
        : null;

  return (
    <View className="flex-1 bg-surface">
      <View style={{ paddingTop: insets.top }} className="bg-surface-container-low">
        <View className="h-topbar flex-row items-center gap-1 pl-1 pr-3">
          <IconButton
            icon="close"
            label={t("common.cancel")}
            onPress={() => router.back()}
            color="on-surface"
          />
          <Text
            variant="title-lg"
            numberOfLines={1}
            accessibilityRole="header"
            className="flex-1 text-on-surface"
          >
            {existing ? t("filing.editTitle") : t("filing.newTitle")}
          </Text>
          <Button
            label={t("filing.save")}
            busy={saving}
            disabled={!tag.trim() || !folder.trim() || !!problem || (!!id && !existing)}
            onPress={() => void save()}
          />
        </View>
      </View>
      <ScrollView
        keyboardShouldPersistTaps="handled"
        contentContainerStyle={{ paddingBottom: insets.bottom + 32 }}
        contentContainerClassName="gap-5 px-4 pt-4"
      >
        {error ? <Notice tone="error" icon="error" text={error} /> : null}
        <View className="gap-2">
          <TextField
            label={t("filing.tag")}
            hint={t("mobile.filing.tagHint")}
            value={tag}
            onChangeText={setTag}
            autoFocus={!id}
            autoCapitalize="none"
            autoCorrect={false}
            returnKeyType="next"
            onSubmitEditing={() => folderRef.current?.focus()}
          />
          {suggestions.length > 0 ? (
            <View className="flex-row flex-wrap gap-2">
              {suggestions.slice(0, 6).map((suggestion) => {
                const picked = suggestion.name.toLowerCase() === tag.trim().toLowerCase();
                return (
                  <Chip
                    key={suggestion.name}
                    icon={picked ? "check" : undefined}
                    label={`${suggestion.name} · ${suggestion.files}`}
                    accessibilityLabel={`${suggestion.name}, ${t("tags.files", { count: suggestion.files })}`}
                    on={picked}
                    onPress={() => setTag(suggestion.name)}
                  />
                );
              })}
            </View>
          ) : null}
        </View>
        <View className="gap-2">
          <TextField
            ref={folderRef}
            label={t("filing.folder")}
            hint={folderError ? undefined : t("filing.folderHint")}
            error={folderError}
            placeholder={t("filing.folderPlaceholder")}
            value={folder}
            onChangeText={(text) => {
              // Typing moves the caret to where it ends; the selection event that follows corrects it.
              selection.current = { start: text.length, end: text.length };
              setFolder(text);
            }}
            onSelectionChange={(event) => (selection.current = event.nativeEvent.selection)}
            autoCapitalize="none"
            autoCorrect={false}
          />
          <View className="flex-row flex-wrap gap-2">
            <Chip
              icon="drive_file_move"
              label={t("mobile.filing.chooseFolder")}
              onPress={() => setPicking(true)}
            />
            <Chip
              icon="add"
              label={t("filing.year")}
              accessibilityLabel={`${t("filing.insert")} ${t("filing.year")}`}
              onPress={() => insert("{year}")}
            />
            <Chip
              icon="add"
              label={t("filing.month")}
              accessibilityLabel={`${t("filing.insert")} ${t("filing.month")}`}
              onPress={() => insert("{month}")}
            />
          </View>
        </View>
        {examples.length > 0 ? (
          <View
            accessibilityLiveRegion="polite"
            className="gap-2 rounded-md bg-surface-container-low p-3"
          >
            <View className="flex-row items-center gap-2">
              <Icon name="drive_file_move" size={20} color="primary" />
              <Text variant="title-sm" className="text-on-surface">
                {t("filing.example")}
              </Text>
            </View>
            {examples.map((example) => (
              <View key={example.nodeId} className="flex-row items-center gap-2">
                <FileTypeIcon kind={kindOf(example.name, example.mimeType)} size={24} />
                <Text
                  variant="body-md"
                  numberOfLines={1}
                  className="min-w-0 flex-1 text-on-surface"
                >
                  {example.name}
                </Text>
                <Text
                  variant="body-sm"
                  numberOfLines={1}
                  className="max-w-[45%] text-on-surface-variant"
                >
                  {example.folder}
                </Text>
              </View>
            ))}
          </View>
        ) : null}
        {files > 0 ? (
          <Pressable
            accessibilityRole="checkbox"
            accessibilityState={{ checked: fileExisting }}
            onPress={() => setFileExisting((on) => !on)}
            className="flex-row items-start gap-3 rounded-md py-1 active:bg-surface-container-high"
          >
            <Icon
              name={fileExisting ? "check_box" : "check_box_outline_blank"}
              filled={fileExisting}
              color={fileExisting ? "primary" : "on-surface-variant"}
            />
            <View className="flex-1">
              <Text className="text-on-surface">{t("filing.fileExisting", { count: files })}</Text>
              <Text variant="body-sm" className="text-on-surface-variant">
                {t("filing.fileExistingHint")}
              </Text>
            </View>
          </Pressable>
        ) : null}
      </ScrollView>
      <Modal
        visible={picking}
        animationType="slide"
        onRequestClose={() => setPicking(false)}
        statusBarTranslucent
      >
        {/* A Modal renders outside the themed tree, so it sets the theme's variables again. */}
        <View style={[{ flex: 1 }, theme.vars]}>
          <FolderPicker
            title={t("mobile.filing.pickTitle")}
            start={null}
            submitLabel={t("mobile.filing.pickHere")}
            blocked={(folderId) => folderId === null}
            onCancel={() => setPicking(false)}
            onSubmit={(picked) => {
              setPicking(false);
              const folders = [...picked.path.split(" / ").slice(1), picked.name];
              setFolder((old) => pickedTemplate(folders, old));
            }}
          />
        </View>
      </Modal>
    </View>
  );
}

/** A filter-style chip: a suggestion to pick, or a button that adds to the folder. */
function Chip({
  label,
  icon,
  on,
  onPress,
  accessibilityLabel,
}: {
  label: string;
  icon?: IconName;
  on?: boolean;
  onPress: () => void;
  accessibilityLabel?: string;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={accessibilityLabel ?? label}
      accessibilityState={on === undefined ? undefined : { selected: on }}
      onPress={onPress}
      className={`h-8 flex-row items-center gap-1.5 rounded-sm px-3 active:opacity-70 ${on ? "bg-secondary-container" : "border border-outline-variant"}`}
    >
      {icon ? (
        <Icon name={icon} size={18} color={on ? "on-secondary-container" : "on-surface-variant"} />
      ) : null}
      <Text
        variant="label-lg"
        className={on ? "text-on-secondary-container" : "text-on-surface-variant"}
      >
        {label}
      </Text>
    </Pressable>
  );
}
