import type { Person } from "@dokument/api";
import { useQueries, useQueryClient } from "@tanstack/react-query";
import { router, useLocalSearchParams } from "expo-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { View } from "react-native";
import { move } from "../../src/state/changes";
import { drive, nodeQuery } from "../../src/state/queries";
import { EmptyState } from "../../src/ui/EmptyState";
import { TopBar } from "../../src/ui/TopBar";
import { Button } from "../../src/ui/Button";
import { FolderPicker } from "../../src/ui/FolderPicker";
import { Sheet, useKept } from "../../src/ui/Sheet";
import { Text } from "../../src/ui/Text";

/**
 * Picks a folder to move files and folders to (docs/design.md §15.3), starting where the first one is. The ones being
 * moved can't be opened. Before a move that would hide something from people who see it now, it asks (§9).
 */
export default function Move() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { ids = "", from } = useLocalSearchParams<{ ids?: string; from?: string }>();
  const queries = useQueries({
    queries: ids
      .split(",")
      .filter(Boolean)
      .map((id) => nodeQuery(id)),
  });
  const nodes = queries.every((query) => query.data)
    ? queries.map((query) => query.data!.node)
    : [];
  const [busy, setBusy] = useState(false);
  const [target, setTarget] = useState<string | null>(null);
  const [losing, setLosing] = useState<Person[] | null>(null);
  const shownLosing = useKept(losing);
  const moving = new Set(nodes.map((node) => node.id));
  // Something shared with the person at its own level sits in a folder they can't see, so there's nowhere to start.
  const outOfSight = queries.some(
    (query) =>
      !!query.data?.node.parentId && query.data.path.at(-1)?.id !== query.data.node.parentId,
  );

  const finish = async (folderId: string | null) => {
    setBusy(true);
    setLosing(null);
    await move(t, queryClient, nodes, folderId);
    router.back();
  };

  const moveHere = async (folderId: string | null) => {
    setBusy(true);
    const people = await drive.losingAccess([...moving], folderId).catch(() => []);
    if (people.length > 0) {
      setBusy(false);
      setTarget(folderId);
      setLosing(people);
    } else await finish(folderId);
  };

  if (outOfSight) {
    return (
      <View className="flex-1 bg-surface">
        <TopBar title={t("actions.move")} back />
        <EmptyState icon="block" title={t("mobile.move.outOfSight")} />
      </View>
    );
  }

  return (
    <FolderPicker
      title={t("dialogs.move.title", {
        count: ids.split(",").filter(Boolean).length,
        name: nodes[0]?.name ?? "",
      })}
      start={from || null}
      submitLabel={t("dialogs.move.here")}
      busy={busy}
      unavailable={moving}
      blocked={(folderId) =>
        nodes.length === 0 || nodes.every((node) => (node.parentId ?? null) === folderId)
      }
      onCancel={() => router.back()}
      onSubmit={(folder) => void moveHere(folder.id)}
    >
      <Sheet
        open={losing !== null}
        onClose={() => setLosing(null)}
        title={t("dialogs.accessLost.title")}
      >
        <View className="mx-5 gap-4">
          <Text className="text-on-surface-variant">
            {shownLosing
              ? t("dialogs.accessLost.text", {
                  count: nodes.length,
                  // Hermes has no Intl.ListFormat.
                  names: shownLosing.map((person) => person.name).join(", "),
                })
              : ""}
          </Text>
          <View className="flex-row justify-end gap-2">
            <Button variant="text" label={t("common.cancel")} onPress={() => setLosing(null)} />
            <Button label={t("dialogs.accessLost.move")} onPress={() => void finish(target)} />
          </View>
        </View>
      </Sheet>
    </FolderPicker>
  );
}
