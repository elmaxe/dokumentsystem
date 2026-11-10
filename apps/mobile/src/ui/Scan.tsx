import { useNetInfo } from "@react-native-community/netinfo";
import { useQueryClient } from "@tanstack/react-query";
import * as DocumentPicker from "expo-document-picker";
import * as ImagePicker from "expo-image-picker";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Platform, Pressable, View } from "react-native";
import DocumentScanner from "react-native-document-scanner-plugin";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { pdfName, scanName } from "../lib/scanPdf";
import { createFolder } from "../state/changes";
import { uploadFiles, uploadScan, type PickedFile } from "../state/uploads";
import { Button } from "./Button";
import { Icon } from "./Icon";
import { Notice } from "./Notice";
import { PdfTaskRows } from "./PdfTasks";
import { NameSheet } from "./NameSheet";
import { Sheet, SheetAction } from "./Sheet";
import { toast } from "./Snackbar";
import { Text } from "./Text";
import { TextField } from "./TextField";
import { UploadTray } from "./UploadTray";

/** iOS can't show a picker while the sheet is still sliding away. */
const sheetGone = () => new Promise((resolve) => setTimeout(resolve, 350));

/** A name for a photo the picker gave none, from the end of its address. */
const nameOf = (uri: string) => decodeURIComponent(uri.split("/").pop() ?? "") || "photo.jpg";

/**
 * The New button in a folder and what it starts (docs/design.md §15.3): a sheet with Upload photos and videos, Upload
 * files, New folder and Scan a document. Uploads show in the tray above the button. Scanning opens the system's document scanner (VisionKit on iOS, ML Kit on Android) with its own edge finding,
 * cropping and page list, then asks for a name for the PDF, then uploads it, with its progress above the button. The
 * server reads the text as it does any scan.
 */
export function NewButton({
  folderId,
  folderName,
  safeArea,
}: {
  folderId: string | null;
  folderName: string;
  /** Whether it sits at the screen's bottom edge, rather than above the navigation bar. */
  safeArea: boolean;
}) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const insets = useSafeAreaInsets();
  const net = useNetInfo();
  const [pages, setPages] = useState<string[] | null>(null);
  const [name, setName] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [menu, setMenu] = useState(false);
  const [naming, setNaming] = useState(0);

  // Offline, the offline notice takes this place: uploads and merges wait for a connection anyway. The scanner is native
  // only, but the progress rows still show without it.
  if (net.isConnected === false) return null;
  const scanner = Platform.OS !== "web";

  const scan = async () => {
    setMenu(false);
    setError(null);
    try {
      const result = await DocumentScanner.scanDocument({ croppedImageQuality: 85 });
      if (result.status !== "success" || !result.scannedImages?.length) return;
      setName(scanName(t("mobile.scan.defaultName"), new Date()));
      setPages(result.scannedImages);
    } catch {
      setError(t("mobile.scan.scannerFailed"));
      setPages([]);
    }
  };

  const upload = async (files: PickedFile[]) => {
    if (files.length === 0) return;
    try {
      await uploadFiles(files, { id: folderId, name: folderName });
    } catch {
      toast(t("errors.generic"));
    }
  };

  // The system pickers need no permission: they hand over only what the person picks.
  const pickPhotos = async () => {
    setMenu(false);
    await sheetGone();
    const result = await ImagePicker.launchImageLibraryAsync({
      mediaTypes: ["images", "videos"],
      allowsMultipleSelection: true,
      selectionLimit: 0,
      quality: 1,
      // The original bytes, HEIC and all, as the camera saved them.
      preferredAssetRepresentationMode: ImagePicker.UIImagePickerPreferredAssetRepresentationMode.Current,
      videoExportPreset: ImagePicker.VideoExportPreset.Passthrough,
    });
    if (result.canceled) return;
    await upload(
      result.assets.map((asset) => ({ uri: asset.uri, name: asset.fileName || nameOf(asset.uri), mimeType: asset.mimeType })),
    );
  };

  const pickFiles = async () => {
    setMenu(false);
    await sheetGone();
    const result = await DocumentPicker.getDocumentAsync({ multiple: true, copyToCacheDirectory: true, type: "*/*" });
    if (result.canceled) return;
    await upload(
      result.assets.map((asset) => ({
        uri: asset.uri,
        name: asset.name,
        mimeType: asset.mimeType,
        modifiedAt: asset.lastModified || null,
      })),
    );
  };

  const close = () => {
    setPages(null);
    setError(null);
    setBusy(false);
  };

  const save = async () => {
    if (!pages?.length || !name.trim()) return;
    setBusy(true);
    try {
      await uploadScan(pages, pdfName(name), { id: folderId, name: folderName });
      close();
    } catch {
      setBusy(false);
      setError(t("mobile.scan.makeFailed"));
    }
  };

  return (
    <>
      <View pointerEvents="box-none" style={{ bottom: 16 + (safeArea ? insets.bottom : 0) }} className="absolute left-3 right-3 items-end gap-2">
        <PdfTaskRows />
        <UploadTray />
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t("mobile.new.buttonLabel", { folder: folderName })}
          onPress={() => setMenu(true)}
          className="h-14 flex-row items-center gap-3 rounded-lg bg-primary-container px-4 shadow-elevation-3 active:opacity-80"
        >
          <Icon name="add" color="on-primary-container" />
          <Text variant="label-lg" className="text-on-primary-container">
            {t("mobile.new.button")}
          </Text>
        </Pressable>
      </View>
      <Sheet open={menu} onClose={() => setMenu(false)} title={t("mobile.new.title", { folder: folderName })}>
        <SheetAction icon={<Icon name="add_photo_alternate" />} label={t("mobile.new.photos")} onPress={() => void pickPhotos()} />
        <SheetAction icon={<Icon name="upload_file" />} label={t("mobile.new.files")} onPress={() => void pickFiles()} />
        <View className="my-2 h-px bg-outline-variant" />
        <SheetAction
          icon={<Icon name="create_new_folder" />}
          label={t("mobile.new.folder")}
          onPress={() => {
            setMenu(false);
            setNaming((n) => n + 1);
          }}
        />
        {scanner ? (
          <SheetAction icon={<Icon name="document_scanner" />} label={t("mobile.new.scan")} onPress={() => void scan()} />
        ) : null}
      </Sheet>
      {naming > 0 ? (
        <NameSheet
          key={naming}
          open
          onClose={() => setNaming(0)}
          title={t("dialogs.newFolder.title")}
          label={t("dialogs.newFolder.label")}
          submitLabel={t("dialogs.newFolder.create")}
          initial=""
          onSubmit={(folder) => createFolder(t, queryClient, folderId, folder)}
        />
      ) : null}
      <Sheet open={pages !== null} onClose={close} title={pages?.length ? t("mobile.scan.title") : undefined}>
        <View className="mx-5 gap-4">
          {error ? <Notice tone="error" icon="error" text={error} /> : null}
          {pages?.length ? (
            <>
              <Text variant="body-md" className="text-on-surface-variant">
                {t("mobile.scan.pages", { count: pages.length, folder: folderName })}
              </Text>
              <TextField
                label={t("mobile.scan.name")}
                value={name}
                onChangeText={setName}
                autoFocus
                selectTextOnFocus
                returnKeyType="done"
                onSubmitEditing={() => void save()}
              />
              <View className="flex-row justify-end gap-2">
                <Button variant="text" label={t("common.cancel")} onPress={close} />
                <Button label={t("mobile.scan.save")} icon="upload" busy={busy} disabled={!name.trim()} onPress={() => void save()} />
              </View>
            </>
          ) : (
            <View className="flex-row justify-end">
              <Button variant="text" label={t("common.close")} onPress={close} />
            </View>
          )}
        </View>
      </Sheet>
    </>
  );
}
