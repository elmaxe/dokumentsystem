import { kindOf } from "@dokument/core";
import { useTranslation } from "react-i18next";
import { View } from "react-native";
import {
  resolveConflict,
  resolveDuplicates,
  useUploads,
  type ConflictPrompt,
  type DuplicatePrompt,
} from "../state/uploads";
import { Button } from "./Button";
import { FileTypeIcon } from "./FileTypeIcon";
import { Icon } from "./Icon";
import { Sheet, SheetAction } from "./Sheet";
import { Text } from "./Text";

const listed = 3;

/**
 * The questions an upload asks (docs/design.md §8.3), one at a time, wherever the person is: files they already have,
 * then names already taken in the folder. Mounted once, behind sign-in.
 */
export function UploadPrompts() {
  const { prompt, items } = useUploads();
  if (!prompt) return null;
  return prompt.kind === "duplicates" ? (
    <DuplicateSheet key={prompt.key} prompt={prompt} />
  ) : (
    <ConflictSheet key={prompt.key} prompt={prompt} names={prompt.ids.map((id) => items.find((item) => item.id === id))} />
  );
}

/** "Upload all" adds them without sending a byte, so skipping is only about clutter; it's the suggested answer. */
function DuplicateSheet({ prompt }: { prompt: DuplicatePrompt }) {
  const { t } = useTranslation();
  const { items } = useUploads();
  const single = prompt.ids.length === 1 && prompt.total === 1;
  const where = (id: string) => {
    const match = prompt.matches[id];
    const path = [t("nav.myFiles"), ...(match?.path ?? []).map((entry) => entry.name)].join(" / ");
    return match?.inTrash ? t("duplicates.inTrash", { path }) : t("duplicates.in", { path });
  };

  return (
    <Sheet
      open
      onClose={() => void resolveDuplicates("skip")}
      title={
        single
          ? t("duplicates.singleTitle")
          : t("duplicates.batchTitle", { count: prompt.ids.length, total: prompt.total })
      }
    >
      <View className="gap-1">
        <Text variant="body-md" className="mx-5 mb-2 text-on-surface-variant">
          {t("duplicates.batchBody")}
        </Text>
        {prompt.ids.slice(0, listed).map((id) => {
          const item = items.find((upload) => upload.id === id);
          return (
            <View key={id} className="min-h-14 flex-row items-center gap-3 px-5">
              <View className="h-10 w-10 items-center justify-center rounded-sm bg-surface-container-highest">
                <FileTypeIcon kind={kindOf(item?.name ?? "", item?.mimeType)} />
              </View>
              <View className="min-w-0 flex-1">
                <Text variant="title-sm" numberOfLines={1} className="text-on-surface">
                  {item?.name}
                </Text>
                <Text variant="body-sm" numberOfLines={1} className="text-on-surface-variant">
                  {where(id)}
                </Text>
              </View>
            </View>
          );
        })}
        {prompt.ids.length > listed ? (
          <Text variant="body-sm" className="px-5 pl-[72px] text-on-surface-variant">
            {t("duplicates.andMore", { count: prompt.ids.length - listed })}
          </Text>
        ) : null}
        <View className="mx-5 mt-3 flex-row justify-end gap-2">
          <Button
            variant="text"
            label={single ? t("duplicates.uploadAnyway") : t("duplicates.uploadAll")}
            onPress={() => void resolveDuplicates("upload")}
          />
          <Button
            label={single ? t("duplicates.skip") : t("duplicates.skipDuplicates")}
            onPress={() => void resolveDuplicates("skip")}
          />
        </View>
      </View>
    </Sheet>
  );
}

function ConflictSheet({ prompt, names }: { prompt: ConflictPrompt; names: ({ name: string } | undefined)[] }) {
  const { t } = useTranslation();
  const count = prompt.ids.length;
  return (
    <Sheet
      open
      onClose={() => resolveConflict("skip")}
      title={t("dialogs.conflict.title", { count, name: names[0]?.name ?? "" })}
    >
      <Text variant="body-md" className="mx-5 mb-2 text-on-surface-variant">
        {count > 1 ? t("dialogs.conflict.bodyAll", { count }) : t("dialogs.conflict.body")}
      </Text>
      <SheetAction
        icon={<Icon name="history" />}
        label={count > 1 ? t("dialogs.conflict.newVersions") : t("dialogs.conflict.newVersion")}
        onPress={() => resolveConflict("newVersion")}
      />
      <SheetAction icon={<Icon name="content_copy" />} label={t("dialogs.conflict.keepBoth")} onPress={() => resolveConflict("keepBoth")} />
      <SheetAction icon={<Icon name="block" />} label={t("dialogs.conflict.skip")} onPress={() => resolveConflict("skip")} />
    </Sheet>
  );
}
