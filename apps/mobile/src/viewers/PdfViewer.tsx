import { useQuery } from "@tanstack/react-query";
import { Directory, File, Paths } from "expo-file-system";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { ActivityIndicator, View } from "react-native";
import { authHeaders } from "../state/api";
import { useTheme } from "../state/theme";
import { EmptyState } from "../ui/EmptyState";
import PdfDom from "./PdfDom";

/** Larger PDFs open in another app: the whole file crosses into the web view at once. */
export const pdfLimit = 50 * 1024 * 1024;

/**
 * A PDF, or an Office document as the PDF the server made of it (docs/design.md §14). The file is downloaded to the
 * cache, where it stays for the next look, and handed to pdf.js in a web view.
 */
export function PdfViewer({ uri, cacheKey }: { uri: string; cacheKey: string }) {
  const { t } = useTranslation();
  const theme = useTheme();
  const [broken, setBroken] = useState(false);
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
  return (
    <PdfDom
      data={file.data}
      background={theme.color("surface-container-highest")}
      // Filled in by the web view as pages scroll by.
      pageLabel={t("viewer.page", { page: "{{page}}", total: "{{total}}" })}
      onFailed={async () => setBroken(true)}
      dom={{ style: { flex: 1 }, matchContents: false }}
    />
  );
}
