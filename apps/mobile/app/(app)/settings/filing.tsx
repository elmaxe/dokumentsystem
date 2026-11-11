import type { FilingRule } from "@dokument/api";
import { exampleFolder, templateLabel } from "@dokument/core";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { router } from "expo-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import {
  ActivityIndicator,
  Pressable,
  RefreshControl,
  ScrollView,
  Switch,
  View,
} from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { filing, filingRulesQuery } from "../../../src/state/filing";
import { useTheme } from "../../../src/state/theme";
import { Button, IconButton } from "../../../src/ui/Button";
import { Icon } from "../../../src/ui/Icon";
import { Notice } from "../../../src/ui/Notice";
import { Sheet, SheetAction, useKept } from "../../../src/ui/Sheet";
import { toast } from "../../../src/ui/Snackbar";
import { Text } from "../../../src/ui/Text";
import { TopBar } from "../../../src/ui/TopBar";

/**
 * The person's filing rules (docs/design.md §13.6, §15.3): when one of their files gets a tag, move it to a folder,
 * once. The first rule that matches wins, so a rule's sheet moves it up or down.
 */
export default function FilingRules() {
  const { t } = useTranslation();
  const theme = useTheme();
  const insets = useSafeAreaInsets();
  const queryClient = useQueryClient();
  const rules = useQuery(filingRulesQuery);
  const [chosen, setChosen] = useState<FilingRule | null>(null);
  const [deleting, setDeleting] = useState<FilingRule | null>(null);
  const shown = useKept(chosen);
  const doomed = useKept(deleting);
  const list = rules.data ?? [];

  const attempt = async (change: () => Promise<unknown>) => {
    try {
      await change();
    } catch {
      toast(t("errors.generic"));
    }
    await queryClient.invalidateQueries({ queryKey: filingRulesQuery.queryKey });
  };
  const switchRule = (rule: FilingRule, enabled: boolean) => {
    // The switch moves at once; the refetch afterwards puts it back if the change didn't take.
    queryClient.setQueryData(filingRulesQuery.queryKey, (old) =>
      old?.map((other) => (other.id === rule.id ? { ...other, enabled } : other)),
    );
    return attempt(() => filing.update(rule.id, { tag: rule.tag, folder: rule.folder, enabled }));
  };
  const move = (rule: FilingRule, by: number) => {
    const ids = list.map((other) => other.id);
    const at = ids.indexOf(rule.id);
    ids.splice(at, 1);
    ids.splice(at + by, 0, rule.id);
    void attempt(() => filing.reorder(ids));
  };
  const edit = (rule: FilingRule | null) =>
    router.push({ pathname: "/settings/filing-rule", params: rule ? { id: rule.id } : {} });
  const index = shown ? list.findIndex((rule) => rule.id === shown.id) : -1;

  return (
    <View className="flex-1 bg-surface">
      <TopBar title={t("filing.title")} back />
      <ScrollView
        contentContainerStyle={{ paddingBottom: insets.bottom + 96 }}
        contentContainerClassName="gap-4 px-4 pt-4"
        refreshControl={
          <RefreshControl
            refreshing={rules.isRefetching}
            onRefresh={() => void rules.refetch()}
            colors={[theme.color("primary")]}
          />
        }
      >
        <Text variant="body-md" className="text-on-surface-variant">
          {t("filing.intro")}
        </Text>
        {rules.isError && !rules.data ? (
          <Notice tone="error" icon="error" text={t("filing.failed")} />
        ) : rules.isPending ? (
          <ActivityIndicator color={theme.color("primary")} className="py-8" />
        ) : list.length === 0 ? (
          <Text variant="body-md" className="py-6 text-center text-on-surface-variant">
            {t("filing.empty")}
          </Text>
        ) : (
          <>
            <View className="overflow-hidden rounded-md border border-outline-variant bg-surface-container-lowest">
              {list.map((rule, at) => (
                <RuleRow
                  key={rule.id}
                  rule={rule}
                  divider={at > 0}
                  onOpen={() => edit(rule)}
                  onMore={() => setChosen(rule)}
                  onEnabled={(enabled) => void switchRule(rule, enabled)}
                />
              ))}
            </View>
            <Text variant="body-sm" className="text-on-surface-variant">
              {t("filing.note")}
            </Text>
          </>
        )}
      </ScrollView>
      <View
        pointerEvents="box-none"
        style={{ bottom: insets.bottom + 16 }}
        className="absolute right-4"
      >
        <Pressable
          accessibilityRole="button"
          onPress={() => edit(null)}
          className="h-14 flex-row items-center gap-3 rounded-lg bg-primary-container px-4 shadow-elevation-3 active:opacity-80"
        >
          <Icon name="add" color="on-primary-container" />
          <Text variant="label-lg" className="text-on-primary-container">
            {t("filing.add")}
          </Text>
        </Pressable>
      </View>

      <Sheet open={!!chosen} onClose={() => setChosen(null)}>
        {shown ? (
          <>
            <View className="mx-5 mb-1 flex-row flex-wrap items-center gap-2">
              <TagChip name={shown.tag} />
              <Icon name="chevron_right" color="outline" size={20} />
              <Text variant="title-md" className="flex-shrink text-on-surface">
                {templateLabel(shown.folder, { year: t("filing.year"), month: t("filing.month") })}
              </Text>
            </View>
            <Text variant="body-sm" className="mx-5 mb-2 text-on-surface-variant">
              {t("mobile.filing.position", { index: index + 1, count: list.length })}
            </Text>
            <SheetAction
              icon={<Icon name="edit" />}
              label={t("filing.edit")}
              onPress={() => {
                setChosen(null);
                edit(shown);
              }}
            />
            {index > 0 ? (
              <SheetAction
                icon={<Icon name="arrow_upward" />}
                label={t("filing.moveUp")}
                onPress={() => {
                  setChosen(null);
                  move(shown, -1);
                }}
              />
            ) : null}
            {index >= 0 && index < list.length - 1 ? (
              <SheetAction
                icon={<Icon name="arrow_downward" />}
                label={t("filing.moveDown")}
                onPress={() => {
                  setChosen(null);
                  move(shown, 1);
                }}
              />
            ) : null}
            <View className="my-2 h-px bg-outline-variant" />
            <SheetAction
              icon={<Icon name="delete" color="error" />}
              label={t("filing.delete")}
              danger
              onPress={() => {
                setChosen(null);
                setDeleting(shown);
              }}
            />
          </>
        ) : null}
      </Sheet>

      <Sheet
        open={!!deleting}
        onClose={() => setDeleting(null)}
        title={doomed ? t("filing.deleteTitle", { tag: doomed.tag }) : undefined}
      >
        <View className="mx-5 gap-4">
          <Text className="text-on-surface-variant">{t("filing.deleteText")}</Text>
          <View className="flex-row justify-end gap-2">
            <Button variant="text" label={t("common.cancel")} onPress={() => setDeleting(null)} />
            <Button
              variant="danger"
              label={t("filing.delete")}
              onPress={() => {
                const rule = doomed!;
                setDeleting(null);
                void attempt(async () => {
                  await filing.remove(rule.id);
                  toast(t("filing.deleted", { tag: rule.tag }));
                });
              }}
            />
          </View>
        </View>
      </Sheet>
    </View>
  );
}

/** One rule: its tag, where it moves files, what it has done, and its switch. */
function RuleRow({
  rule,
  divider,
  onOpen,
  onMore,
  onEnabled,
}: {
  rule: FilingRule;
  divider: boolean;
  onOpen: () => void;
  onMore: () => void;
  onEnabled: (enabled: boolean) => void;
}) {
  const { t } = useTranslation();
  const theme = useTheme();
  const folder = templateLabel(rule.folder, { year: t("filing.year"), month: t("filing.month") });
  return (
    <View
      className={`flex-row items-center gap-2 py-3 pl-4 pr-1 ${divider ? "border-t border-outline-variant" : ""}`}
    >
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={`${rule.tag}, ${folder}`}
        accessibilityHint={t("filing.edit")}
        onPress={onOpen}
        onLongPress={onMore}
        className="min-w-0 flex-1 items-start gap-1.5 active:opacity-70"
      >
        <TagChip name={rule.tag} />
        <View className="flex-row items-center gap-1.5">
          <Icon name="folder" size={20} />
          <Text
            variant="title-sm"
            numberOfLines={2}
            className={`flex-shrink ${rule.enabled ? "text-on-surface" : "text-on-surface-variant"}`}
          >
            {folder}
          </Text>
        </View>
        {rule.running ? (
          <View className="flex-row items-center gap-1">
            <ActivityIndicator size="small" color={theme.color("warning")} />
            <Text variant="body-sm" className="text-warning">
              {t("filing.running")}
            </Text>
          </View>
        ) : (
          <Text variant="body-sm" className="text-on-surface-variant">
            {rule.enabled
              ? t("filing.filedCount", { count: rule.filed, folder: exampleFolder(rule.folder) })
              : t("filing.off")}
          </Text>
        )}
        {rule.enabled && rule.skipped > 0 ? (
          <View className="flex-row items-start gap-1">
            <Icon name="group" size={16} />
            <Text variant="body-sm" className="flex-1 text-on-surface-variant">
              {t("filing.skipped", { count: rule.skipped })}
            </Text>
          </View>
        ) : null}
      </Pressable>
      <Switch
        accessibilityLabel={t("filing.enabled", { tag: rule.tag })}
        value={rule.enabled}
        onValueChange={onEnabled}
        trackColor={{
          true: theme.color("primary"),
          false: theme.color("surface-container-highest"),
        }}
        thumbColor={theme.color(rule.enabled ? "on-primary" : "outline")}
      />
      <IconButton icon="more_vert" label={t("filing.more", { tag: rule.tag })} onPress={onMore} />
    </View>
  );
}

function TagChip({ name }: { name: string }) {
  return (
    <View className="h-8 justify-center rounded-sm bg-secondary-container px-3">
      <Text variant="label-lg" className="text-on-secondary-container">
        {name}
      </Text>
    </View>
  );
}
