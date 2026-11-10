import { kindOf } from "@dokument/core";
import AsyncStorage from "@react-native-async-storage/async-storage";
import { router } from "expo-router";
import { useShareIntentContext, type ShareIntentFile } from "expo-share-intent";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { FlatList, Pressable, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { useFormat } from "../../src/state/format";
import { uploadFiles } from "../../src/state/uploads";
import { Button, IconButton } from "../../src/ui/Button";
import { EmptyState } from "../../src/ui/EmptyState";
import { FileTypeIcon } from "../../src/ui/FileTypeIcon";
import { FolderPicker, type PickedFolder } from "../../src/ui/FolderPicker";
import { Icon } from "../../src/ui/Icon";
import { Notice } from "../../src/ui/Notice";
import { Text } from "../../src/ui/Text";

const lastFolderKey = "dokument.shareFolder";

/**
 * Files shared to the app from another one (docs/design.md §15.3): the list, with a way to leave some out, and the
 * folder they go to, which starts as the one used last time. Saving uploads them like any others and opens the folder,
 * where the tray shows them going up.
 */
export default function Share() {
  const { t } = useTranslation();
  const format = useFormat();
  const insets = useSafeAreaInsets();
  const { shareIntent, resetShareIntent } = useShareIntentContext();
  const [left, setLeft] = useState<Set<string>>(new Set());
  const [folder, setFolder] = useState<PickedFolder>({
    id: null,
    name: t("nav.myFiles"),
    path: "",
  });
  const [remembered, setRemembered] = useState(false);
  const [picking, setPicking] = useState(false);
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    void AsyncStorage.getItem(lastFolderKey).then((saved) => {
      if (!saved) return;
      try {
        setFolder(JSON.parse(saved) as PickedFolder);
        setRemembered(true);
      } catch {
        // Starts in My files.
      }
    });
  }, []);

  const files = (shareIntent.files ?? []).filter((file) => !left.has(file.path));
  const size = files.reduce((sum, file) => sum + (file.size ?? 0), 0);
  const nameOf = (file: ShareIntentFile) =>
    file.fileName || decodeURIComponent(file.path.split("/").pop() ?? "");

  const close = () => {
    resetShareIntent();
    if (router.canGoBack()) router.back();
    else router.replace("/");
  };

  const save = async () => {
    setBusy(true);
    setFailed(false);
    try {
      await uploadFiles(
        files.map((file) => ({ uri: file.path, name: nameOf(file), mimeType: file.mimeType })),
        { id: folder.id, name: folder.name },
      );
    } catch {
      setBusy(false);
      setFailed(true);
      return;
    }
    void AsyncStorage.setItem(lastFolderKey, JSON.stringify(folder));
    resetShareIntent();
    if (folder.id) router.replace({ pathname: "/folder/[id]", params: { id: folder.id } });
    else router.replace("/");
  };

  if (picking) {
    return (
      <FolderPicker
        title={t("mobile.share.saveIn")}
        start={folder.id}
        submitLabel={t("mobile.share.saveHere")}
        onCancel={() => setPicking(false)}
        onSubmit={(picked) => {
          setFolder(picked);
          setRemembered(false);
          setPicking(false);
        }}
      />
    );
  }

  return (
    <View className="flex-1 bg-surface" style={{ paddingTop: insets.top }}>
      <View className="h-topbar flex-row items-center gap-1 pl-1 pr-2">
        <IconButton icon="close" label={t("common.cancel")} onPress={close} color="on-surface" />
        <Text
          variant="title-lg"
          numberOfLines={1}
          accessibilityRole="header"
          className="flex-1 text-on-surface"
        >
          {t("mobile.share.title")}
        </Text>
      </View>
      {shareIntent.files === null || (shareIntent.files.length === 0 && left.size === 0) ? (
        <EmptyState icon="upload_file" title={t("mobile.share.onlyFiles")} />
      ) : (
        <FlatList
          data={files}
          keyExtractor={(file) => file.path}
          ListHeaderComponent={
            <Text variant="title-sm" className="px-4 pb-1 pt-2 text-on-surface-variant">
              {t("mobile.share.summary", { count: files.length, size: format.size(size) })}
            </Text>
          }
          ListFooterComponent={
            <View className="gap-1 pt-4">
              <Text variant="title-sm" className="px-4 pb-1 text-on-surface-variant">
                {t("mobile.share.saveIn")}
              </Text>
              <Pressable
                accessibilityRole="button"
                accessibilityLabel={t("mobile.share.change", { folder: folder.name })}
                onPress={() => setPicking(true)}
                className="mx-4 min-h-16 flex-row items-center gap-4 rounded-md border border-outline-variant bg-surface-container-lowest pl-4 pr-2 active:bg-surface-container-highest"
              >
                <FileTypeIcon kind="folder" />
                <View className="min-w-0 flex-1">
                  <Text numberOfLines={1} className="text-on-surface">
                    {folder.name}
                  </Text>
                  {folder.path || remembered ? (
                    <Text variant="body-sm" numberOfLines={1} className="text-on-surface-variant">
                      {[folder.path, remembered ? t("mobile.share.lastUsed") : ""]
                        .filter(Boolean)
                        .join(" · ")}
                    </Text>
                  ) : null}
                </View>
                <Icon name="chevron_right" />
              </Pressable>
            </View>
          }
          renderItem={({ item }) => (
            <View className="min-h-14 flex-row items-center gap-4 pl-4 pr-1">
              <FileTypeIcon kind={kindOf(nameOf(item), item.mimeType)} />
              <View className="min-w-0 flex-1">
                <Text numberOfLines={1} className="text-on-surface">
                  {nameOf(item)}
                </Text>
                {item.size !== null ? (
                  <Text variant="body-sm" className="text-on-surface-variant">
                    {format.size(item.size)}
                  </Text>
                ) : null}
              </View>
              <IconButton
                icon="close"
                label={t("mobile.share.leaveOut", { name: nameOf(item) })}
                onPress={() => setLeft(new Set([...left, item.path]))}
              />
            </View>
          )}
        />
      )}
      <View
        style={{ paddingBottom: insets.bottom + 12 }}
        className="gap-3 bg-surface-container px-4 pt-3"
      >
        {failed ? <Notice tone="error" icon="error" text={t("errors.generic")} /> : null}
        <View className="flex-row justify-end gap-2">
          <Button variant="text" label={t("common.cancel")} onPress={close} />
          <Button
            label={t("mobile.share.save", { count: files.length })}
            icon="upload"
            busy={busy}
            disabled={files.length === 0}
            onPress={() => void save()}
          />
        </View>
      </View>
    </View>
  );
}
