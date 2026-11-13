import { kindOf } from "@dokument/core";
import type { TFunction } from "i18next";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { View } from "react-native";
import { useFormat } from "../state/format";
import {
  clearFinished,
  dismissUpload,
  pauseUpload,
  resumeUpload,
  useUploads,
  type Upload,
  type UploadState,
} from "../state/uploads";
import { Button, IconButton } from "./Button";
import { FileTypeIcon } from "./FileTypeIcon";
import { Icon } from "./Icon";
import { Text } from "./Text";

const active: UploadState[] = ["checking", "waiting", "uploading", "paused"];
/** The rows that matter most come first: what's moving, then what needs the person. */
const order: UploadState[] = ["uploading", "failed", "checking", "paused", "skipped", "waiting", "done"];
const shown = 4;

/**
 * The upload tray above the New button (docs/design.md §15.3): one card for everything uploading, with the files
 * done, the bytes sent and the time left, and the first few files with their own progress. It folds down to its
 * header, and clears itself a moment after the last upload is done.
 */
export function UploadTray() {
  const { t } = useTranslation();
  const format = useFormat();
  const uploads = useUploads();
  const [folded, setFolded] = useState(false);
  const items = uploads.items;
  const busy = items.some((item) => active.includes(item.state));
  const failed = items.filter((item) => item.state === "failed").length;
  const settled = !busy && failed === 0 && !uploads.prompt;

  useEffect(() => {
    if (!settled || items.length === 0) return;
    const timer = setTimeout(clearFinished, 4_000);
    return () => clearTimeout(timer);
  }, [settled, items.length]);

  if (items.length === 0) return null;

  const counted = items.filter((item) => item.state !== "skipped");
  const done = items.filter((item) => item.state === "done" || item.state === "skipped").length;
  const size = counted.reduce((sum, item) => sum + item.size, 0);
  const sent = counted.reduce((sum, item) => sum + (item.state === "done" ? item.size : item.sent), 0);
  const left = uploads.secondsLeft;
  const progress = [
    t("mobile.uploads.files", { done, total: items.length }),
    ...(uploads.resumed
      ? [t("mobile.uploads.pickingUp")]
      : [
          t("mobile.uploads.bytes", { sent: format.size(sent), size: format.size(size) }),
          ...(left !== undefined && left >= 1
            ? [
                left < 60
                  ? t("upload.secondsLeft", { count: Math.ceil(left) })
                  : t("upload.minutesLeft", { count: Math.ceil(left / 60) }),
              ]
            : []),
        ]),
  ].join(" · ");

  const sorted = [...items].sort((a, b) => order.indexOf(a.state) - order.indexOf(b.state));
  const rows = sorted.slice(0, shown);
  const hidden = sorted.slice(shown);
  const percent = size > 0 ? Math.floor((sent / size) * 100) : 0;

  return (
    <View accessibilityLiveRegion="polite" className="w-full rounded-lg bg-surface-container-high py-1 shadow-elevation-3">
      <View className="min-h-16 flex-row items-center gap-3 py-2 pl-4 pr-1">
        <View className="min-w-0 flex-1 gap-0.5">
          <Text variant="title-md" className="text-on-surface">
            {busy ? t("upload.uploading", { count: items.length }) : t("upload.finished", { count: done })}
          </Text>
          <Text variant="body-sm" numberOfLines={2} className={failed && !busy ? "text-error" : "text-on-surface-variant"}>
            {busy ? progress : failed ? t("upload.group.failed", { count: failed }) : progress}
          </Text>
          {busy ? <Bar percent={percent} /> : null}
        </View>
        {busy ? (
          <IconButton
            icon={folded ? "keyboard_arrow_up" : "keyboard_arrow_down"}
            label={t(folded ? "upload.expand" : "upload.collapse")}
            onPress={() => setFolded(!folded)}
          />
        ) : (
          <IconButton icon="close" label={t("upload.close")} onPress={clearFinished} />
        )}
      </View>
      {folded ? null : (
        <>
          <View className="mx-4 h-px bg-outline-variant" />
          {rows.map((upload) => (
            <UploadRow key={upload.id} upload={upload} />
          ))}
          {hidden.length > 0 ? (
            <Text variant="body-sm" className="py-1 pl-16 pr-4 text-on-surface-variant">
              {hidden.every((item) => item.state === "waiting")
                ? t("upload.group.waiting", { count: hidden.length })
                : t("mobile.uploads.more", { count: hidden.length })}
            </Text>
          ) : null}
        </>
      )}
    </View>
  );
}

function Bar({ percent }: { percent: number }) {
  return (
    <View className="mt-1.5 h-1 overflow-hidden rounded-full bg-surface-container-highest">
      <View style={{ width: `${percent}%` }} className="h-1 rounded-full bg-primary" />
    </View>
  );
}

/** One file: its progress, why it stopped, or where it went. */
function UploadRow({ upload }: { upload: Upload }) {
  const { t } = useTranslation();
  const percent = upload.size > 0 ? Math.floor((upload.sent / upload.size) * 100) : 0;
  const name = upload.name;
  const status = (() => {
    switch (upload.state) {
      case "checking":
        return t("upload.status.hashing");
      case "waiting":
        return t("upload.status.waiting");
      case "uploading":
        return t("mobile.uploads.uploading", { percent });
      case "paused":
        return t("mobile.uploads.paused", { percent });
      case "done":
        return t("upload.status.done", { folder: upload.folderName });
      case "skipped":
        return t("mobile.uploads.skipped", {
          path: [t("nav.myFiles"), ...(upload.duplicate?.path ?? []).map((entry) => entry.name)].join(" / "),
        });
      case "failed":
        return upload.error === undefined ? t("mobile.uploads.connection") : uploadFailure(t, upload.error);
    }
  })();
  const tone =
    upload.state === "failed" ? "text-error" : upload.state === "skipped" ? "text-warning" : "text-on-surface-variant";

  return (
    <View className="min-h-16 flex-row items-center gap-3 py-2 pl-4 pr-1">
      <View className="h-10 w-10 items-center justify-center rounded-sm bg-surface-container-highest">
        {upload.state === "failed" ? (
          <Icon name="error" color="error" filled />
        ) : upload.state === "done" ? (
          <Icon name="check_circle" color="success" filled />
        ) : (
          <FileTypeIcon kind={kindOf(name, upload.mimeType)} />
        )}
      </View>
      <View className="min-w-0 flex-1">
        <Text variant="title-sm" numberOfLines={1} className="text-on-surface">
          {name}
        </Text>
        <Text variant="body-sm" numberOfLines={2} className={tone}>
          {status}
        </Text>
        {upload.state === "uploading" || upload.state === "paused" ? <Bar percent={percent} /> : null}
      </View>
      {upload.state === "uploading" ? (
        <IconButton icon="pause_circle" label={t("mobile.uploads.pause", { name })} onPress={() => pauseUpload(upload.id)} />
      ) : upload.state === "paused" ? (
        <IconButton icon="play_circle" label={t("mobile.uploads.resume", { name })} onPress={() => resumeUpload(upload.id)} />
      ) : upload.state === "failed" ? (
        <Button variant="text" label={t("mobile.tryAgain")} accessibilityLabel={t("mobile.uploads.retry", { name })} onPress={() => resumeUpload(upload.id)} />
      ) : null}
      {upload.state !== "uploading" && upload.state !== "paused" ? (
        <IconButton
          icon="close"
          compact
          label={t(active.includes(upload.state) ? "mobile.uploads.stop" : "mobile.uploads.hide", { name })}
          onPress={() => dismissUpload(upload.id)}
        />
      ) : null}
    </View>
  );
}

export function uploadFailure(t: TFunction, code: string): string {
  switch (code) {
    case "quotaExceeded":
    case "notFound":
    case "nameTaken":
    case "invalidName":
    case "forbidden":
    case "changedWhileUploading":
      return t(`errors.${code}`);
    default:
      return t("errors.generic");
  }
}
