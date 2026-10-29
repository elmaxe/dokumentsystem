import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useLocalSearchParams } from "expo-router";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { ActivityIndicator, View } from "react-native";
import { previewOf, type Preview } from "../../../src/state/preview";
import { drive, invalidateStars, nodeQuery } from "../../../src/state/queries";
import { useTheme } from "../../../src/state/theme";
import { IconButton } from "../../../src/ui/Button";
import { EmptyState } from "../../../src/ui/EmptyState";
import { ItemSheet } from "../../../src/ui/ItemSheet";
import { Notice } from "../../../src/ui/Notice";
import { TopBar } from "../../../src/ui/TopBar";
import { ImageViewer } from "../../../src/viewers/ImageViewer";
import { MediaViewer } from "../../../src/viewers/MediaViewer";
import { PdfViewer } from "../../../src/viewers/PdfViewer";
import { TextViewer } from "../../../src/viewers/TextViewer";

/** While the worker prepares a preview or a playable copy, the file is fetched again until it's ready. */
const waiting = (preview: Preview) => preview.type === "preparing" || preview.type === "converting";

/**
 * A file opened from a list (docs/design.md §14, §15.3): its preview full screen, with the star, and the file's
 * actions, among them Open in another app for what the app can't show.
 */
export default function FileScreen() {
  const { id } = useLocalSearchParams<{ id: string }>();
  const { t } = useTranslation();
  const theme = useTheme();
  const queryClient = useQueryClient();
  const [refused, setRefused] = useState(false);
  const [actions, setActions] = useState(false);
  const details = useQuery({
    ...nodeQuery(id),
    refetchInterval: (query) => {
      const node = query.state.data?.node;
      return node && waiting(previewOf(node, false)) ? 3_000 : false;
    },
  });
  const node = details.data?.node;

  // For the person's Recent page only; opening a preview is never audited (§18).
  useEffect(() => {
    drive.opened(id).catch(() => {});
  }, [id]);

  // A video the phone can't play as it is: ask for its copy now, which starts it or moves it ahead of background work.
  const copy = node?.media?.copy;
  const needsCopy = copy === "none" || copy === "waiting";
  useEffect(() => {
    if (!needsCopy) return;
    drive
      .requestPlayable(id)
      .then((next) =>
        queryClient.setQueryData(nodeQuery(id).queryKey, (old) =>
          old ? { ...old, node: next } : old,
        ),
      )
      .catch(() => setRefused(true));
  }, [needsCopy, id, queryClient]);

  const star = async () => {
    if (!node) return;
    try {
      await drive.star([node.id], !node.starred);
    } catch {
      // The star shows its old state again after the refetch, so the failure is visible.
    }
    await invalidateStars(queryClient);
  };

  const body = () => {
    if (details.isError && !node) return <EmptyState icon="error" title={t("errors.generic")} />;
    if (!node) {
      return (
        <View className="flex-1 items-center justify-center">
          <ActivityIndicator
            color={theme.color("primary")}
            accessibilityLabel={t("viewer.loading")}
          />
        </View>
      );
    }
    const preview = previewOf(node, refused);
    const cacheKey = `${node.id}/${node.currentVersionId ?? ""}`;
    switch (preview.type) {
      case "image":
        return <ImageViewer uri={preview.uri} cacheKey={`${cacheKey}/preview`} label={node.name} />;
      case "video":
      case "audio":
        return <MediaViewer key={preview.uri} uri={preview.uri} audio={preview.type === "audio"} />;
      case "pdf":
        return (
          <View className="flex-1">
            {node.shownAsPdf ? (
              <View className="p-3">
                <Notice icon="info" text={t("mobile.viewer.shownAsPdf")} />
              </View>
            ) : null}
            <PdfViewer uri={preview.uri} cacheKey={cacheKey} />
          </View>
        );
      case "text":
      case "markdown":
        return (
          <TextViewer
            uri={preview.uri}
            markdown={preview.type === "markdown"}
            cacheKey={cacheKey}
            size={node.size ?? 0}
          />
        );
      case "preparing":
        return (
          <EmptyState
            icon="schedule"
            title={t("viewer.preparingTitle")}
            text={t("viewer.preparingText")}
          />
        );
      case "converting":
        return (
          <EmptyState
            icon="movie"
            title={t("viewer.convertingTitle")}
            text={
              preview.waiting
                ? t("mobile.viewer.convertingWaiting")
                : t("mobile.viewer.convertingText")
            }
          >
            {preview.progress != null ? (
              <Notice
                icon="progress_activity"
                text={t("mobile.viewer.convertingProgress", { percent: preview.progress })}
              />
            ) : null}
          </EmptyState>
        );
      case "unplayable":
        return (
          <EmptyState
            icon="error"
            title={t("viewer.unplayableTitle")}
            text={t("mobile.viewer.unplayableText")}
          />
        );
      case "none":
        return (
          <EmptyState
            icon="draft"
            title={t("viewer.noPreviewTitle")}
            text={t("mobile.viewer.noPreviewText")}
          />
        );
    }
  };

  return (
    <View className="flex-1 bg-surface">
      <TopBar
        title={node?.name ?? ""}
        back
        actions={
          node ? (
            <>
              <IconButton
                icon="star"
                filled={node.starred}
                label={node.starred ? t("actions.unstar") : t("actions.star")}
                onPress={() => void star()}
              />
              <IconButton
                icon="more_vert"
                label={t("files.moreActionsFor", { name: node.name })}
                onPress={() => setActions(true)}
              />
            </>
          ) : null
        }
      />
      {body()}
      <ItemSheet
        node={actions ? (node ?? null) : null}
        onClose={() => setActions(false)}
        onOpenFolder={() => {}}
      />
    </View>
  );
}
