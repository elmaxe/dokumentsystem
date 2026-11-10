import { useQuery } from "@tanstack/react-query";
import { Directory, File, Paths } from "expo-file-system";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { ActivityIndicator, View } from "react-native";
import { authHeaders } from "../state/api";
import { useTheme } from "../state/theme";
import { EmptyState } from "../ui/EmptyState";
import { MatchBar } from "./MatchBar";
import PdfDom from "./PdfDom";

/** What a PDF opened from search marks: the query, its terms, and the pages that match (docs/design.md §13.3). */
export type PdfSearch = {
  query: string;
  terms: string[];
  pages: { pageNo: number; boxes: number[][] }[];
  /** The best page, to open on. */
  start?: number;
  onClear: () => void;
};

/** Larger PDFs open in another app: the whole file crosses into the web view at once. */
export const pdfLimit = 50 * 1024 * 1024;

/**
 * A PDF, or an Office document as the PDF the server made of it (docs/design.md §14). The file is downloaded to the
 * cache, where it stays for the next look, and handed to pdf.js in a web view.
 */
export function PdfViewer({ uri, cacheKey, search }: { uri: string; cacheKey: string; search?: PdfSearch | null }) {
  const { t } = useTranslation();
  const theme = useTheme();
  const [broken, setBroken] = useState(false);
  const [current, setCurrent] = useState(1);
  const [target, setTarget] = useState(() =>
    search?.start || search?.pages[0] ? { page: search.start ?? search.pages[0]!.pageNo, key: 0 } : null,
  );
  const file = useQuery({
    queryKey: ["pdf", cacheKey],
    queryFn: async () => {
      const folder = new Directory(Paths.cache, "preview");
      folder.create({ intermediates: true, idempotent: true });
      const target = new File(folder, `${cacheKey.replaceAll("/", "_")}.pdf`);
      if (!target.exists)
        await File.downloadFileAsync(uri, target, { headers: authHeaders(), idempotent: true });
      if ((target.size ?? 0) > pdfLimit) return null;
      return target.base64();
    },
    staleTime: Infinity,
    gcTime: 60_000,
  });

  if (file.isError || broken) {
    return (
      <EmptyState
        icon="error"
        title={t("viewer.failedTitle")}
        text={t("mobile.viewer.failedText")}
      />
    );
  }
  if (file.data === null) {
    return (
      <EmptyState
        icon="picture_as_pdf"
        title={t("mobile.viewer.tooLargeTitle")}
        text={t("mobile.viewer.tooLargeText")}
      />
    );
  }
  if (file.isPending) {
    return (
      <View className="flex-1 items-center justify-center">
        <ActivityIndicator
          color={theme.color("primary")}
          accessibilityLabel={t("viewer.loading")}
        />
      </View>
    );
  }
  const hits = (search?.pages ?? []).map((page) => page.pageNo).sort((a, b) => a - b);
  const position = hits.indexOf(current);
  const previous = [...hits].reverse().find((page) => page < current);
  const next = hits.find((page) => page > current);
  const go = (page: number | undefined) =>
    page === undefined ? undefined : () => setTarget({ page, key: (target?.key ?? 0) + 1 });

  return (
    <View className="flex-1">
    <PdfDom
      data={file.data}
      background={theme.color("surface-container-highest")}
      // Filled in by the web view as pages scroll by.
      pageLabel={t("viewer.page", { page: "{{page}}", total: "{{total}}" })}
      onFailed={async () => setBroken(true)}
      marks={
        search
          ? {
              terms: search.terms,
              pages: search.pages,
              fill: theme.color("warning-container"),
              edge: theme.color("warning"),
            }
          : null
      }
      target={target}
      onPage={async (page) => setCurrent(page)}
      dom={{ style: { flex: 1 }, matchContents: false }}
    />
      {search ? (
        <MatchBar
          query={search.query}
          detail={
            position >= 0
              ? t("mobile.viewer.matchPage", { page: current, current: position + 1, total: hits.length })
              : t("viewer.noMatchesHere")
          }
          previous={{ label: t("viewer.previousMatch"), onPress: go(previous) }}
          next={{ label: t("viewer.nextMatch"), onPress: go(next) }}
          onClose={search.onClear}
        />
      ) : null}
    </View>
  );
}
