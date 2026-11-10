import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import * as WebBrowser from "expo-web-browser";
import { useTranslation } from "react-i18next";
import { ActivityIndicator, View } from "react-native";
import { authHeaders } from "../state/api";
import { useFormat } from "../state/format";
import { useTheme } from "../state/theme";
import { EmptyState } from "../ui/EmptyState";
import { Notice } from "../ui/Notice";
import { MatchBar } from "./MatchBar";
import TextDom from "./TextDom";

/** The first megabyte is enough to read on a phone; longer files say so and open in another app. */
const limit = 1024 * 1024;

/** A text or Markdown file, read with a range request and shown in a web view (docs/design.md §15.3). */
export function TextViewer({
  uri,
  markdown,
  cacheKey,
  size,
  search,
}: {
  uri: string;
  markdown: boolean;
  cacheKey: string;
  size: number;
  /** Opened from search: the words to mark (docs/design.md §13.3). */
  search?: { query: string; terms: string[]; onClear: () => void } | null;
}) {
  const [marked, setMarked] = useState<number | null>(null);
  const { t } = useTranslation();
  const theme = useTheme();
  const format = useFormat();
  const text = useQuery({
    queryKey: ["text", cacheKey],
    queryFn: async () => {
      const response = await fetch(uri, {
        headers: { ...authHeaders(), Range: `bytes=0-${limit - 1}` },
      });
      if (!response.ok) throw new Error(`Preview failed with ${response.status}`);
      return response.text();
    },
    // Kept with the version in its key, so it never goes stale; not saved for offline use.
    staleTime: Infinity,
    gcTime: 5 * 60_000,
  });

  if (text.isError) {
    return (
      <EmptyState
        icon="error"
        title={t("viewer.failedTitle")}
        text={t("mobile.viewer.failedText")}
      />
    );
  }
  if (text.isPending) {
    return (
      <View className="flex-1 items-center justify-center bg-surface">
        <ActivityIndicator
          color={theme.color("primary")}
          accessibilityLabel={t("viewer.loading")}
        />
      </View>
    );
  }

  return (
    <View className="flex-1 bg-surface">
      {size > limit ? (
        <View className="p-3">
          <Notice icon="info" text={t("mobile.viewer.truncated", { size: format.size(limit) })} />
        </View>
      ) : null}
      <TextDom
        text={text.data}
        markdown={markdown}
        colors={{
          background: theme.color("surface"),
          text: theme.color("on-surface"),
          muted: theme.color("on-surface-variant"),
          link: theme.color("primary"),
          code: theme.color("surface-container-high"),
          border: theme.color("outline-variant"),
          mark: theme.color("warning-container"),
        }}
        terms={search?.terms}
        onMarks={async (count) => setMarked(count)}
        imageLabel={t("mobile.viewer.markdownImage", { alt: "{{alt}}" })}
        openLink={async (url) => {
          await WebBrowser.openBrowserAsync(url);
        }}
        dom={{ style: { flex: 1 }, matchContents: false }}
      />
      {search ? (
        <MatchBar
          query={search.query}
          detail={marked === null ? undefined : t("mobile.viewer.marked", { count: marked })}
          onClose={search.onClear}
        />
      ) : null}
    </View>
  );
}
