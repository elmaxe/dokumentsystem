import type { NodeDto, Tag } from "@dokument/api";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Pressable, View } from "react-native";
import { changeTags, invalidateTags, tagSuggestionsQuery } from "../state/info";
import { Button } from "./Button";
import { Icon } from "./Icon";
import { Sheet } from "./Sheet";
import { toast } from "./Snackbar";
import { Text } from "./Text";
import { TextField } from "./TextField";

type Counted = Tag & { files: number };

const key = (name: string) => name.toLowerCase();

/**
 * Tags for several files at once, as the web's Tags dialog (docs/design.md §13.4): the tags all of them have, those
 * only some have ("1 of 3", with + to give it to all and × to take it off all), and a field to add one to all.
 * Nothing changes until Save, and the snackbar offers Undo. Give it a new key each time it opens.
 */
export function TagsSheet({
  nodes,
  open,
  onClose,
  onSaved,
}: {
  nodes: NodeDto[];
  open: boolean;
  onClose: () => void;
  /** After a save, such as ending the selection. */
  onSaved?: () => void;
}) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [add, setAdd] = useState<string[]>([]);
  const [remove, setRemove] = useState<string[]>([]);
  const [text, setText] = useState("");
  const [saving, setSaving] = useState(false);
  const count = nodes.length;
  const typed = text.trim();
  const suggestions = useQuery(tagSuggestionsQuery(typed)).data ?? [];

  const counts = new Map<string, Counted>();
  for (const node of nodes) {
    for (const tag of node.tags ?? []) {
      const seen = counts.get(key(tag.name));
      counts.set(key(tag.name), seen ? { ...seen, files: seen.files + 1 } : { ...tag, files: 1 });
    }
  }
  const tags = [...counts.values()].sort((a, b) => a.name.localeCompare(b.name));
  const removing = new Set(remove.map(key));
  const adding = new Set(add.map(key));
  const shown = tags
    .filter((tag) => !removing.has(key(tag.name)))
    .map((tag) => (adding.has(key(tag.name)) ? { ...tag, files: count } : tag));
  const extra = add.filter((name) => !counts.has(key(name)));
  const onAll: Counted[] = [
    ...shown.filter((tag) => tag.files >= count),
    ...extra.map((name): Counted => ({ name, source: "user", ruleId: null, files: count })),
  ];
  const onSome = shown.filter((tag) => tag.files < count);
  const changes = add.length + remove.length;
  const tooLong = text.split(",").some((name) => name.trim().length > 50);
  const offered = suggestions.filter((tag) => !onAll.some((other) => key(other.name) === key(tag.name))).slice(0, 5);

  const addTag = (name: string) => {
    setRemove((list) => list.filter((other) => key(other) !== key(name)));
    setAdd((list) => (list.some((other) => key(other) === key(name)) ? list : [...list, name]));
  };
  const removeTag = (name: string) => {
    setAdd((list) => list.filter((other) => key(other) !== key(name)));
    if (counts.has(key(name))) setRemove((list) => [...list, name]);
  };
  const addTyped = (names: string[]) => {
    names.map((name) => name.trim()).filter(Boolean).forEach(addTag);
    setText("");
  };

  const save = async () => {
    // A tag typed but not yet added counts too, so Save doesn't drop it.
    const typedNames = tooLong ? [] : text.split(",").map((name) => name.trim()).filter(Boolean);
    const added = [...add, ...typedNames.filter((name) => !add.some((other) => key(other) === key(name)))];
    const removed = remove.filter((name) => !added.some((other) => key(other) === key(name)));
    const ids = nodes.map((node) => node.id);
    setSaving(true);
    try {
      await changeTags(ids, { add: added, remove: removed });
      toast(t("toasts.tagsChanged", { count }), {
        label: t("common.undo"),
        onPress: () =>
          void changeTags(ids, { add: removed, remove: added })
            .catch(() => toast(t("errors.generic")))
            .finally(() => void invalidateTags(queryClient)),
      });
    } catch {
      toast(t("errors.generic"));
    }
    await invalidateTags(queryClient);
    setSaving(false);
    onClose();
    onSaved?.();
  };

  return (
    <Sheet open={open} onClose={onClose} title={t("tags.dialogTitle", { count })}>
      <View className="mx-5 gap-4">
        {count > 1 ? (
          <Text variant="body-sm" className="text-on-surface-variant">
            {t("tags.dialogHint")}
          </Text>
        ) : null}
        {onAll.length > 0 ? (
          <View className="gap-2">
            <Text variant="label-lg" className="text-on-surface-variant">
              {t("tags.onAll", { count })}
            </Text>
            <View className="flex-row flex-wrap gap-2">
              {onAll.map((tag) => (
                <View key={tag.name} className="h-8 flex-row items-center rounded-sm bg-secondary-container pl-3">
                  {tag.source !== "user" ? <Icon name="auto_awesome" size={16} color="on-secondary-container" /> : null}
                  <Text variant="label-lg" className="text-on-secondary-container">
                    {tag.name}
                  </Text>
                  <ChipButton icon="close" label={`${t("tags.removeFromAll")}: ${tag.name}`} onPress={() => removeTag(tag.name)} />
                </View>
              ))}
            </View>
          </View>
        ) : null}
        {onSome.length > 0 ? (
          <View className="gap-2">
            <Text variant="label-lg" className="text-on-surface-variant">
              {t("tags.onSome")}
            </Text>
            <View className="flex-row flex-wrap gap-2">
              {onSome.map((tag) => (
                <View key={tag.name} className="h-8 flex-row items-center gap-0.5 rounded-sm border border-dashed border-outline pl-3">
                  {tag.source !== "user" ? <Icon name="auto_awesome" size={16} /> : null}
                  <Text variant="label-lg" className="text-on-surface">
                    {t("tags.someCount", { name: tag.name, count: tag.files, total: count })}
                  </Text>
                  <ChipButton icon="add" label={`${t("tags.addToAll")}: ${tag.name}`} onPress={() => addTag(tag.name)} />
                  <ChipButton icon="close" label={`${t("tags.removeFromAll")}: ${tag.name}`} onPress={() => removeTag(tag.name)} />
                </View>
              ))}
            </View>
          </View>
        ) : null}
        {onAll.length === 0 && onSome.length === 0 ? (
          <Text variant="body-md" className="text-on-surface-variant">
            {t("tags.none")}
          </Text>
        ) : null}
        <TextField
          label={t("tags.addToAllLabel", { count })}
          hint={t("mobile.info.addTagHint")}
          error={tooLong ? t("tags.tooLong") : null}
          value={text}
          onChangeText={setText}
          autoCapitalize="none"
          autoCorrect={false}
          returnKeyType="done"
          submitBehavior="submit"
          onSubmitEditing={() => !tooLong && addTyped(text.split(","))}
        />
        {typed && offered.length > 0 ? (
          <View className="flex-row flex-wrap gap-2">
            {offered.map((tag) => (
              <Pressable
                key={tag.name}
                accessibilityRole="button"
                accessibilityLabel={`${tag.name}, ${t("tags.files", { count: tag.files })}`}
                onPress={() => addTyped([...text.split(",").slice(0, -1), tag.name])}
                className="h-8 justify-center rounded-sm border border-outline px-3 active:bg-surface-container-highest"
              >
                <Text variant="label-lg" className="text-on-surface-variant">
                  {`${tag.name} · ${tag.files}`}
                </Text>
              </Pressable>
            ))}
          </View>
        ) : null}
        <View className="flex-row items-center justify-end gap-2">
          {changes > 0 ? (
            <Text variant="body-sm" className="flex-1 text-on-surface-variant">
              {t("tags.pending", { count: changes })}
            </Text>
          ) : null}
          <Button variant="text" label={t("common.cancel")} onPress={onClose} />
          <Button
            label={t("common.save")}
            busy={saving}
            disabled={(changes === 0 && !typed) || tooLong}
            onPress={() => void save()}
          />
        </View>
      </View>
    </Sheet>
  );

}

function ChipButton({ icon, label, onPress }: { icon: "add" | "close"; label: string; onPress: () => void }) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={label}
      onPress={onPress}
      hitSlop={4}
      className="h-8 w-8 items-center justify-center rounded-sm active:bg-surface-container-highest"
    >
      <Icon name={icon} size={18} color={icon === "add" ? "primary" : "on-surface-variant"} />
    </Pressable>
  );
}
