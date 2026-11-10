import { useNetInfo } from "@react-native-community/netinfo";
import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { Platform, Pressable, View } from "react-native";
import DocumentScanner from "react-native-document-scanner-plugin";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { pdfName, scanName } from "../lib/scanPdf";
import { createFolder } from "../state/changes";
import { dismissUpload, retryUpload, uploadScan, useUploads, type Upload } from "../state/uploads";
import { Button, IconButton } from "./Button";
import { Icon } from "./Icon";
import { Notice } from "./Notice";
import { PdfTaskRows } from "./PdfTasks";
import { NameSheet } from "./NameSheet";
import { Sheet, SheetAction } from "./Sheet";
import { Text } from "./Text";
import { TextField } from "./TextField";

/**
 * The New button in a folder and what it starts (docs/design.md §15.3): a sheet with New folder and Scan a document.
 * Scanning opens the system's document scanner (VisionKit on iOS, ML Kit on Android) with its own edge finding,
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
  const uploads = useUploads().filter((upload) => upload.folderId === folderId);

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

  const close = () => {
    setPages(null);
    setError(null);
    setBusy(false);
  };

  const save = async () => {
    if (!pages?.length || !name.trim()) return;
    setBusy(true);
    try {
      await uploadScan(pages, pdfName(name), folderId, queryClient);
      close();
    } catch {
      setBusy(false);
      setError(t("mobile.scan.makeFailed"));
    }
  };

  return (
    <>
      <View pointerEvents="box-none" style={{ bottom: 16 + (safeArea ? insets.bottom : 0) }} className="absolute left-3 right-3 items-end gap-2">
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
        <PdfTaskRows />
        {uploads.map((upload) => (
          <UploadRow key={upload.id} upload={upload} onRetry={() => retryUpload(upload.id, queryClient)} />
        ))}
      </View>
      <Sheet open={menu} onClose={() => setMenu(false)} title={t("mobile.new.title", { folder: folderName })}>
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

/** One upload: its progress, or why it failed with a way to try again. A finished one hides itself after a moment. */
function UploadRow({ upload, onRetry }: { upload: Upload; onRetry: () => void }) {
  const { t } = useTranslation();
  const done = upload.state === "done";
  useEffect(() => {
    if (!done) return;
    const timer = setTimeout(() => dismissUpload(upload.id), 4_000);
    return () => clearTimeout(timer);
  }, [done, upload.id]);

  const percent = upload.size > 0 ? Math.floor((upload.sent / upload.size) * 100) : 0;
  const status = (() => {
    switch (upload.state) {
      case "uploading":
        return t("mobile.uploads.uploading", { percent });
      case "done":
        return t("mobile.uploads.done");
      case "failed":
        return upload.error === undefined ? t("mobile.uploads.connection") : failure(t, upload.error);
    }
  })();

  return (
    <View
      accessibilityLiveRegion="polite"
      className="w-full gap-2 rounded-md bg-surface-container-high px-4 py-3 shadow-elevation-2"
    >
      <View className="flex-row items-center gap-3">
        <Icon
          name={done ? "check_circle" : upload.state === "failed" ? "error" : "upload"}
          color={done ? "success" : upload.state === "failed" ? "error" : "on-surface-variant"}
          filled={upload.state !== "uploading"}
        />
        <View className="min-w-0 flex-1">
          <Text variant="title-sm" numberOfLines={1} className="text-on-surface">
            {upload.name}
          </Text>
          <Text variant="body-sm" className={upload.state === "failed" ? "text-error" : "text-on-surface-variant"}>
            {status}
          </Text>
        </View>
        {upload.state === "failed" ? <Button variant="text" label={t("mobile.tryAgain")} onPress={onRetry} /> : null}
        <IconButton
          icon="close"
          compact
          label={t(upload.state === "uploading" ? "mobile.uploads.stop" : "mobile.uploads.hide", { name: upload.name })}
          onPress={() => dismissUpload(upload.id)}
        />
      </View>
      {upload.state === "uploading" ? (
        <View className="h-1 overflow-hidden rounded-full bg-surface-container-highest">
          <View style={{ width: `${percent}%` }} className="h-1 rounded-full bg-primary" />
        </View>
      ) : null}
    </View>
  );
}

function failure(t: (key: string) => string, code: string): string {
  switch (code) {
    case "quotaExceeded":
      return t("errors.quotaExceeded");
    case "notFound":
      return t("errors.notFound");
    default:
      return t("errors.generic");
  }
}
