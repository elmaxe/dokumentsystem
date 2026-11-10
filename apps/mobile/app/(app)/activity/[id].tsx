import type { Activity } from "@dokument/api";
import { useInfiniteQuery, useQuery } from "@tanstack/react-query";
import { useLocalSearchParams } from "expo-router";
import { useTranslation } from "react-i18next";
import { ActivityIndicator, SectionList, View } from "react-native";
import { kindOfNode, nodeQuery, sessionQuery } from "../../../src/state/queries";
import { activityQuery, activityText } from "../../../src/state/sharing";
import { useTheme } from "../../../src/state/theme";
import { Avatar } from "../../../src/ui/Avatar";
import { Button } from "../../../src/ui/Button";
import { EmptyState } from "../../../src/ui/EmptyState";
import { FileTypeIcon } from "../../../src/ui/FileTypeIcon";
import { Notice } from "../../../src/ui/Notice";
import { Text } from "../../../src/ui/Text";
import { TopBar } from "../../../src/ui/TopBar";

const localeOf = (language: string) => (language === "en" ? "en-GB" : language);
const dayKey = (date: Date) => `${date.getFullYear()}-${date.getMonth()}-${date.getDate()}`;

/**
 * What happened to a file, or to a folder and everything in it (docs/design.md §18), newest first and by day, 30 at a
 * time, as the web's Activity tab. Downloads show only to the owner, and to each person for their own.
 */
export default function ActivityPage() {
  const { id } = useLocalSearchParams<{ id: string }>();
  const { t, i18n } = useTranslation();
  const theme = useTheme();
  const me = useQuery(sessionQuery).data?.user.id;
  const node = useQuery(nodeQuery(id)).data?.node;
  const activity = useInfiniteQuery(activityQuery(id));
  const locale = localeOf(i18n.language);

  const entries = (activity.data?.pages ?? []).flatMap((page) => page.entries);
  const sections = groupByDay(entries, (date) => dayLabel(t, date, locale));
  const time = new Intl.DateTimeFormat(locale, { hour: "2-digit", minute: "2-digit" });

  return (
    <View className="flex-1 bg-surface">
      <TopBar title={t("details.tabs.activity")} back />
      {node ? (
        <View className="flex-row items-center gap-2 bg-surface-container-low px-4 pb-2">
          <FileTypeIcon kind={kindOfNode(node)} size={18} />
          <Text variant="body-md" numberOfLines={1} className="flex-1 text-on-surface-variant">
            {node.kind === "folder" ? t("mobile.activity.folder", { name: node.name }) : node.name}
          </Text>
        </View>
      ) : null}
      {activity.isPending ? (
        <View className="flex-1 items-center justify-center">
          <ActivityIndicator color={theme.color("primary")} />
        </View>
      ) : activity.isError && !activity.data ? (
        <View className="p-4">
          <Notice tone="error" icon="error" text={t("errors.generic")} />
        </View>
      ) : (
        <SectionList
          sections={sections}
          keyExtractor={(entry) => entry.id}
          contentContainerClassName="pb-8"
          stickySectionHeadersEnabled={false}
          ListEmptyComponent={<EmptyState icon="history" title={t("activity.empty")} />}
          renderSectionHeader={({ section }) => (
            <Text
              variant="title-sm"
              accessibilityRole="header"
              className="px-4 pb-1 pt-4 text-on-surface-variant"
            >
              {section.title}
            </Text>
          )}
          renderItem={({ item }) => (
            <View accessible className="flex-row items-start gap-4 px-4 py-2">
              <Avatar name={item.actor?.name ?? "?"} size={32} />
              <View className="min-w-0 flex-1">
                <Text variant="body-md" className="text-on-surface">
                  {activityText(t, item, me)}
                </Text>
                <Text variant="body-sm" className="text-on-surface-variant">
                  {time.format(new Date(item.at))}
                </Text>
              </View>
            </View>
          )}
          ListFooterComponent={
            activity.hasNextPage ? (
              <View className="items-center py-2">
                <Button
                  variant="tonal"
                  label={t("activity.more")}
                  busy={activity.isFetchingNextPage}
                  onPress={() => void activity.fetchNextPage()}
                />
              </View>
            ) : undefined
          }
        />
      )}
    </View>
  );
}

function groupByDay(entries: Activity[], label: (date: Date) => string) {
  const sections: { key: string; title: string; data: Activity[] }[] = [];
  for (const entry of entries) {
    const date = new Date(entry.at);
    const key = dayKey(date);
    const last = sections.at(-1);
    if (last?.key === key) last.data.push(entry);
    else sections.push({ key, title: label(date), data: [entry] });
  }
  return sections;
}

type TFunction = ReturnType<typeof useTranslation>["t"];

/** "Today", "Yesterday", "3 November", or "3 November 2025" before this year. */
function dayLabel(t: TFunction, date: Date, locale: string) {
  const now = new Date();
  if (dayKey(date) === dayKey(now)) return t("mobile.activity.today");
  const yesterday = new Date(now.getFullYear(), now.getMonth(), now.getDate() - 1);
  if (dayKey(date) === dayKey(yesterday)) return t("mobile.activity.yesterday");
  return new Intl.DateTimeFormat(locale, {
    day: "numeric",
    month: "long",
    year: date.getFullYear() === now.getFullYear() ? undefined : "numeric",
  }).format(date);
}
